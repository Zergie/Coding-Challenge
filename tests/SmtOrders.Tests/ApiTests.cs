using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SmtOrders.Api;

namespace SmtOrders.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;
    private readonly string _connection = Environment.GetEnvironmentVariable("SMT_TEST_POSTGRES")
        ?? "Host=localhost;Port=5432;Database=smt_test;Username=smt;Password=smt";

    public async Task InitializeAsync()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _connection);
            builder.UseSetting("Entra:TenantId", "00000000-0000-0000-0000-000000000000");
            builder.UseSetting("Entra:Audience", "api://test");
            builder.UseSetting("Entra:AppIdUri", "api://test");
            builder.UseSetting("Entra:BrowserClientId", "00000000-0000-0000-0000-000000000001");
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        });
        _client = _app.CreateClient();
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            truncate snapshot_components,snapshot_boards,production_snapshots,reservations,
              order_lines,orders,board_recipe,board_revisions,boards,components cascade
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _app.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Anonymous_data_request_is_denied()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/components")).StatusCode);
    }

    [Fact]
    public async Task Reviewer_can_create_and_find_component()
    {
        Authenticate();
        var created = await _client.PostAsJsonAsync("/api/components", new ComponentInput("R-101", "Resistor", "10 kOhm", 100));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var component = await created.Content.ReadFromJsonAsync<ComponentView>();
        Assert.NotNull(component);
        var found = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.Equal(100, found?.AvailableStock);
        var search = await _client.GetFromJsonAsync<List<ComponentView>>("/api/components?q=ohm");
        Assert.Contains(search!, x => x.Id == component.Id);
    }

    [Fact]
    public async Task Reservation_and_download_consume_stock_once_and_retry_bytes_match()
    {
        Authenticate();
        var component = await CreateComponent("C-1", 10);
        var board = await CreateBoard(component.Id);
        var input = new OrderInput("Demo order", "Production demo", new DateOnly(2026, 9, 24), null,
            [new OrderLineInput(board.Id, 1, 2)]);
        var created = await _client.PostAsJsonAsync("/api/orders", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = await created.Content.ReadFromJsonAsync<OrderView>();
        Assert.NotNull(order);
        var reserved = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.Equal(6, reserved?.ReservedStock);
        Assert.Equal(4, reserved?.AvailableStock);
        var firstResponse = await _client.PostAsync($"/api/orders/{order.Id}/download", null);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadAsByteArrayAsync();
        var consumed = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.Equal(4, consumed?.PhysicalStock);
        Assert.Equal(0, consumed?.ReservedStock);
        var catalogEdit = await _client.PutAsJsonAsync($"/api/components/{component.Id}",
            new ComponentInput("C-1", "Part", "Updated after start", 500));
        Assert.Equal(HttpStatusCode.OK, catalogEdit.StatusCode);
        var secondResponse = await _client.PostAsync($"/api/orders/{order.Id}/download", null);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(first, second);
        var after = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.Equal(500, after?.PhysicalStock);
        Assert.Equal(0, after?.ReservedStock);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/orders/{order.Id}")).StatusCode);
    }

    [Fact]
    public async Task Failed_order_and_stock_edits_preserve_the_previous_reservation()
    {
        Authenticate();
        var component = await CreateComponent("C-3", 10);
        var board = await CreateBoard(component.Id);
        var input = new OrderInput("Protected", "Atomic edits", new DateOnly(2026, 9, 24), null,
            [new OrderLineInput(board.Id, 1, 2)]);
        var response = await _client.PostAsJsonAsync("/api/orders", input);
        var order = (await response.Content.ReadFromJsonAsync<OrderView>())!;
        var tooLarge = input with { Boards = [new OrderLineInput(board.Id, 1, 4)] };
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PutAsJsonAsync($"/api/orders/{order.Id}", tooLarge)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PutAsJsonAsync($"/api/components/{component.Id}",
            new ComponentInput("C-3", "Part", "Demo part", 5))).StatusCode);
        var unchanged = await _client.GetFromJsonAsync<OrderView>($"/api/orders/{order.Id}");
        Assert.Equal(2, unchanged?.Boards.Single().BuildQuantity);
        var stock = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.Equal(10, stock?.PhysicalStock);
        Assert.Equal(6, stock?.ReservedStock);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/orders/{order.Id}")).StatusCode);
        stock = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.Equal(0, stock?.ReservedStock);
    }

    [Fact]
    public async Task Board_revision_does_not_silently_change_existing_order()
    {
        Authenticate();
        var component = await CreateComponent("C-4", 20);
        var board = await CreateBoard(component.Id);
        var input = new OrderInput("Pinned", "Recipe history", new DateOnly(2026, 9, 24), null,
            [new OrderLineInput(board.Id, 1, 2)]);
        var orderResponse = await _client.PostAsJsonAsync("/api/orders", input);
        var order = (await orderResponse.Content.ReadFromJsonAsync<OrderView>())!;
        var revisionResponse = await _client.PutAsJsonAsync($"/api/boards/{board.Id}",
            new BoardEdit("Board", "New recipe", 100, 50, [new RecipeInput(component.Id, 1)]));
        Assert.Equal(HttpStatusCode.OK, revisionResponse.StatusCode);
        var old = await _client.GetFromJsonAsync<BoardView>($"/api/boards/{board.Id}/revisions/1");
        Assert.Equal(3, old?.Recipe.Single().QuantityPerBoard);
        var unchanged = await _client.GetFromJsonAsync<OrderView>($"/api/orders/{order.Id}");
        Assert.Equal(1, unchanged?.Boards.Single().Revision);
        var beforeEdit = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.Equal(6, beforeEdit?.ReservedStock);
        var edited = await _client.PutAsJsonAsync($"/api/orders/{order.Id}",
            input with { Boards = [new OrderLineInput(board.Id, 2, 2)] });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var afterEdit = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.Equal(2, afterEdit?.ReservedStock);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/components/{component.Id}")).StatusCode);
    }

    [Fact]
    public async Task Competing_orders_cannot_reserve_more_than_stock()
    {
        Authenticate();
        var component = await CreateComponent("C-2", 5);
        var board = await CreateBoard(component.Id);
        var input = new OrderInput("Parallel", "Reservation race", new DateOnly(2026, 9, 24), null,
            [new OrderLineInput(board.Id, 1, 1)]);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => _client.PostAsJsonAsync("/api/orders", input)));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        var stock = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.Equal(3, stock?.ReservedStock);
        Assert.Equal(2, stock?.AvailableStock);
    }

    private void Authenticate() => _client.DefaultRequestHeaders.Add("X-Test-User", "reviewer");

    private async Task<ComponentView> CreateComponent(string part, long stock)
    {
        var response = await _client.PostAsJsonAsync("/api/components", new ComponentInput(part, "Part", "Demo part", stock));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ComponentView>())!;
    }

    private async Task<BoardView> CreateBoard(Guid componentId)
    {
        var response = await _client.PostAsJsonAsync("/api/boards", new BoardInput(Guid.NewGuid().ToString("N"),
            "Board", "Demo board", 100, 50, [new RecipeInput(componentId, 3)]));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BoardView>())!;
    }
}

public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-User")) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("scp", "access_as_user")], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}
