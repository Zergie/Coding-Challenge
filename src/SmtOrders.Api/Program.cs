using System.Globalization;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;
using SmtOrders.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, services, logger) =>
{
    _ = logger.ReadFrom.Configuration(context.Configuration)
                       .ReadFrom
                       .Services(services).MinimumLevel
                       .Override("Microsoft.AspNetCore", LogEventLevel.Warning).WriteTo
                       .Console(formatProvider: CultureInfo.InvariantCulture);

    var home = Environment.GetEnvironmentVariable("HOME");
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"))
        && !string.IsNullOrWhiteSpace(home))
    {
        _ = logger.WriteTo.File(Path.Combine(home, "LogFiles", "Application", "smt-orders-.log"),
            formatProvider: CultureInfo.InvariantCulture,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: 10 * 1024 * 1024,
            retainedFileCountLimit: 2,
            rollOnFileSizeLimit: true,
            shared: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1));
    }
});

var tableName = builder.Configuration["Storage:TableName"] ?? "SmtOrders";
var connectionString = builder.Configuration["Storage:ConnectionString"];
var tableServiceUri = builder.Configuration["Storage:TableServiceUri"];
var tenantId = builder.Configuration["Entra:TenantId"]
    ?? throw new InvalidOperationException("Entra:TenantId is required.");
var apiAudience = builder.Configuration["Entra:Audience"]
    ?? throw new InvalidOperationException("Entra:Audience is required.");
var appIdUri = builder.Configuration["Entra:AppIdUri"]
    ?? throw new InvalidOperationException("Entra:AppIdUri is required.");
var scope = builder.Configuration["Entra:Scope"]
    ?? throw new InvalidOperationException("Entra:Scope is required.");
var browserClientId = builder.Configuration["Entra:BrowserClientId"]
    ?? throw new InvalidOperationException("Entra:BrowserClientId is required.");

builder.Services.AddSingleton(_ => connectionString is not null
    ? new TableClient(connectionString, tableName)
    : new TableClient(new Uri(tableServiceUri ?? throw new InvalidOperationException(
        "Storage:ConnectionString or Storage:TableServiceUri is required.")), tableName,
        new DefaultAzureCredential()));
builder.Services.AddSingleton<Database>();
builder.Services.AddScoped<ComponentService>();
builder.Services.AddScoped<BoardService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.Audience = apiAudience;
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Authentication").LogWarning("Bearer authentication failed");
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("user", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        context.User.FindFirst("scp")?.Value.Split(' ').Contains(scope) == true) //explain: What does this do? What is scp?
    );
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "SMT Order Management", Version = "v1" });
    options.OperationFilter<PartialUpdateOperationFilter>();
    options.AddSecurityDefinition("Entra", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.OAuth2,
        Flows = new OpenApiOAuthFlows
        {
            AuthorizationCode = new OpenApiOAuthFlow
            {
                AuthorizationUrl = new Uri($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize"),
                TokenUrl = new Uri($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token"),
                Scopes = new Dictionary<string, string> { [$"{appIdUri}/{scope}"] = "Use the SMT order API" }
            }
        }
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Entra" } }]
            = [$"{appIdUri}/{scope}"]
    });
});

var app = builder.Build();
var database = app.Services.GetRequiredService<Database>();
await database.Initialize();

app.UseSerilogRequestLogging();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Errors");
    var (status, code, message) = error switch
    {
        DomainException domain => (domain.Status, domain.Code, domain.Message),
        BadHttpRequestException => (400, "invalid_input", "The request body or route is invalid."),
        _ => (500, "internal_error", "The request could not be completed.")
    };
    if (status >= 500)
    {
        logger.LogError(error, "Request failed");
    }
    else
    {
        logger.LogWarning("Request rejected with {Code}: {Message}", code, message);
    }

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { code, message });
}));
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.OAuthClientId(browserClientId);
    options.OAuthUsePkce();
    options.OAuthScopes($"{appIdUri}/{scope}");
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
var api = app.MapGroup("/api").RequireAuthorization("user");

ComponentService.Map(api);
BoardService.Map(api);
OrderService.Map(api);

app.Run();

public partial class Program;
