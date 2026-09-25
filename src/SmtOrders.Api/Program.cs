using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi.Models;
using Azure.Data.Tables;
using Azure.Identity;
using Serilog;
using SmtOrders.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .WriteTo.Console());

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
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.Audience = apiAudience;
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
builder.Services.AddAuthorization(options => options.AddPolicy("reviewer", policy =>
    policy.RequireAuthenticatedUser().RequireAssertion(context =>
        context.User.FindFirst("scp")?.Value.Split(' ').Contains(scope) == true)));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "SMT Order Management", Version = "v1" });
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
await app.Services.GetRequiredService<Database>().Initialize();
if (args.Contains("--reset-demo"))
{
    await DemoSeed.Reset(app.Services);
    return;
}

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
    if (status >= 500) logger.LogError(error, "Request failed");
    else logger.LogWarning("Request rejected with {Code}: {Message}", code, message);
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
var api = app.MapGroup("/api").RequireAuthorization("reviewer");

api.MapPost("/components", async (ComponentInput input, CatalogService service) =>
{
    var result = await service.CreateComponent(input);
    return Results.Created($"/api/components/{result.Id}", result);
});
api.MapGet("/components", (string? q, CatalogService service) => service.SearchComponents(q));
api.MapGet("/components/{id:guid}", async (Guid id, CatalogService service) =>
    await service.FindComponent(id) is { } result ? Results.Ok(result) : Results.NotFound(new { code = "not_found", message = "Component was not found." }));
api.MapPut("/components/{id:guid}", (Guid id, ComponentInput input, CatalogService service) => service.UpdateComponent(id, input));
api.MapDelete("/components/{id:guid}", async (Guid id, CatalogService service) =>
{ await service.DeleteComponent(id); return Results.NoContent(); });

api.MapPost("/boards", async (BoardInput input, CatalogService service) =>
{
    var result = await service.CreateBoard(input);
    return Results.Created($"/api/boards/{result.Id}/revisions/1", result);
});
api.MapGet("/boards", (string? q, CatalogService service) => service.SearchBoards(q));
api.MapGet("/boards/{id:guid}", async (Guid id, CatalogService service) =>
    await service.FindBoard(id) is { } result ? Results.Ok(result) : Results.NotFound(new { code = "not_found", message = "Board was not found." }));
api.MapGet("/boards/{id:guid}/revisions/{revision:int}", async (Guid id, int revision, CatalogService service) =>
    await service.FindBoard(id, revision) is { } result ? Results.Ok(result) : Results.NotFound(new { code = "not_found", message = "Board revision was not found." }));
api.MapPut("/boards/{id:guid}", (Guid id, BoardEdit input, CatalogService service) => service.ReviseBoard(id, input));
api.MapDelete("/boards/{id:guid}", async (Guid id, CatalogService service) =>
{ await service.DeleteBoard(id); return Results.NoContent(); });

api.MapPost("/orders", async (OrderInput input, OrderService service) =>
{
    var result = await service.Create(input);
    return Results.Created($"/api/orders/{result.Id}", result);
});
api.MapGet("/orders", (string? q, OrderService service) => service.Search(q));
api.MapGet("/orders/{id:guid}", async (Guid id, OrderService service) =>
    await service.Find(id) is { } result ? Results.Ok(result) : Results.NotFound(new { code = "not_found", message = "Order was not found." }));
api.MapPut("/orders/{id:guid}", (Guid id, OrderInput input, OrderService service) => service.Update(id, input));
api.MapDelete("/orders/{id:guid}", async (Guid id, OrderService service) =>
{ await service.Delete(id); return Results.NoContent(); });
api.MapPost("/orders/{id:guid}/download", async (Guid id, OrderService service, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(await service.Download(id), contentType: "application/vnd.smt-production.v1+json");
});

app.Run();

public partial class Program;
