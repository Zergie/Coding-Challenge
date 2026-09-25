using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Azure.Data.Tables;
using SmtOrders.Api;

namespace SmtOrders.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;
    private readonly string _connection = Environment.GetEnvironmentVariable("SMT_TEST_TABLES")
        ?? "UseDevelopmentStorage=true";
    private readonly string _tableName = "SmtTest" + Guid.NewGuid().ToString("N")[..16];

    public Task InitializeAsync()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:ConnectionString", _connection);
            builder.UseSetting("Storage:TableName", _tableName);
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
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _app.Dispose();
        await new TableClient(_connection, _tableName).DeleteAsync();
    }

    [Fact]
    public async Task Anonymous_data_request_is_denied()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/components")).StatusCode);
    }

    [Fact]
    public async Task Fresh_and_cleared_table_have_no_application_records()
    {
        Authenticate();
        Assert.Empty((await _client.GetFromJsonAsync<List<ComponentView>>("/api/components"))!);
        Assert.Empty((await _client.GetFromJsonAsync<List<BoardView>>("/api/boards"))!);
        Assert.Empty((await _client.GetFromJsonAsync<List<OrderView>>("/api/orders"))!);

        var component = await CreateComponent("CLEAR-1", 10);
        var board = await CreateBoard(component.Id);
        var order = await _client.PostAsJsonAsync("/api/orders", new OrderInput("Clear me", "Reset check",
            new DateOnly(2026, 9, 25), null, [new(board.Id, 1, 1)]));
        Assert.Equal(HttpStatusCode.Created, order.StatusCode);

        await _app.Services.GetRequiredService<Database>().Clear();
        Assert.Empty((await _client.GetFromJsonAsync<List<ComponentView>>("/api/components"))!);
        Assert.Empty((await _client.GetFromJsonAsync<List<BoardView>>("/api/boards"))!);
        Assert.Empty((await _client.GetFromJsonAsync<List<OrderView>>("/api/orders"))!);
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
    public async Task Board_revision_list_returns_each_recipe_in_revision_order()
    {
        Authenticate();
        var component = await CreateComponent("REVISION-LIST", 20);
        var board = await CreateBoard(component.Id);
        var second = await _client.PutAsJsonAsync($"/api/boards/{board.Id}",
            new BoardEdit("Second", "Recipe two", 100, 50, [new RecipeInput(component.Id, 1)]));
        var third = await _client.PutAsJsonAsync($"/api/boards/{board.Id}",
            new BoardEdit("Third", "Recipe three", 100, 50, [new RecipeInput(component.Id, 2)]));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);

        var response = await _client.GetAsync($"/api/boards/{board.Id}/revisions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var revisions = (await response.Content.ReadFromJsonAsync<List<BoardView>>())!;
        Assert.Equal([1, 2, 3], revisions.Select(x => x.Revision));
        Assert.All(revisions, revision => Assert.Equal(board.Id, revision.Id));
        Assert.Equal([3, 1, 2], revisions.Select(x => x.Recipe.Single().QuantityPerBoard));

        var missing = await _client.GetAsync($"/api/boards/{Guid.NewGuid()}/revisions");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Order_can_reserve_two_revisions_of_the_same_board()
    {
        Authenticate();
        var component = await CreateComponent("TWO-REVISIONS", 30);
        var board = await CreateBoard(component.Id);
        var revision = await _client.PutAsJsonAsync($"/api/boards/{board.Id}",
            new BoardEdit("Board", "Updated recipe", 100, 50, [new RecipeInput(component.Id, 1)]));
        Assert.Equal(HttpStatusCode.OK, revision.StatusCode);

        var input = new OrderInput("Two revisions", "One Board, two recipes", new DateOnly(2026, 9, 25),
            new DateOnly(2026, 9, 25), [new(board.Id, 2, 4), new(board.Id, 1, 2)]);
        var created = await _client.PostAsJsonAsync("/api/orders", input);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = (await created.Content.ReadFromJsonAsync<OrderView>())!;
        Assert.Equal([1, 2], order.Boards.Select(x => x.Revision));
        Assert.Equal(10, (await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}"))!.ReservedStock);

        var downloaded = await _client.PostAsync($"/api/orders/{order.Id}/download", null);
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        var handoff = (await downloaded.Content.ReadFromJsonAsync<ProductionHandoff>())!;
        Assert.Equal([1, 2], handoff.Boards.Select(x => x.Revision));
        Assert.Equal([6, 4], handoff.Boards.Select(x => x.Components.Single().TotalRequired));
        Assert.Equal(10, handoff.Materials.Single().TotalRequired);
        var afterDownload = (await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}"))!;
        Assert.Equal(20, afterDownload.PhysicalStock);
        Assert.Equal(0, afterDownload.ReservedStock);
    }

    [Fact]
    public async Task Order_edit_accepts_distinct_revisions_but_rejects_a_repeated_pair()
    {
        Authenticate();
        var component = await CreateComponent("EDIT-REVISIONS", 30);
        var board = await CreateBoard(component.Id);
        var revision = await _client.PutAsJsonAsync($"/api/boards/{board.Id}",
            new BoardEdit("Board", "Updated recipe", 100, 50, [new RecipeInput(component.Id, 1)]));
        Assert.Equal(HttpStatusCode.OK, revision.StatusCode);

        var input = new OrderInput("Revision edit", "Pair uniqueness", new DateOnly(2026, 9, 25), null,
            [new(board.Id, 1, 1)]);
        var repeated = input with { Boards = [new(board.Id, 1, 1), new(board.Id, 1, 2)] };
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/orders", repeated)).StatusCode);
        var created = await _client.PostAsJsonAsync("/api/orders", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = (await created.Content.ReadFromJsonAsync<OrderView>())!;

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync($"/api/orders/{order.Id}", repeated)).StatusCode);
        Assert.Equal(3, (await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}"))!.ReservedStock);
        var distinct = input with { Boards = [new(board.Id, 1, 1), new(board.Id, 2, 2)] };
        var edited = await _client.PutAsJsonAsync($"/api/orders/{order.Id}", distinct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal([1, 2], ((await edited.Content.ReadFromJsonAsync<OrderView>())!).Boards.Select(x => x.Revision));
        Assert.Equal(5, (await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}"))!.ReservedStock);
    }

    [Fact]
    public async Task Catalog_validation_search_and_unreferenced_deletion_are_visible_over_http()
    {
        Authenticate();
        var spare = await CreateComponent("SPARE", 5);
        var component = await CreateComponent("USED", 20);
        var invalid = await _client.PostAsJsonAsync("/api/boards", new BoardInput("INVALID", "Bad board",
            "Empty recipe", 80, 50, []));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_input", invalidBody.GetProperty("code").GetString());
        var board = await CreateBoard(component.Id);
        var duplicate = await _client.PostAsJsonAsync("/api/components",
            new ComponentInput("USED", "Duplicate", "Duplicate part", 1));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var components = await _client.GetFromJsonAsync<List<ComponentView>>("/api/components?q=Demo%20part");
        Assert.Contains(components!, x => x.Id == component.Id);
        var boards = await _client.GetFromJsonAsync<List<BoardView>>("/api/boards?q=Demo%20board");
        Assert.Contains(boards!, x => x.Id == board.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/components/{spare.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/components/{component.Id}")).StatusCode);
    }

    [Fact]
    public async Task Production_handoff_has_required_fields_and_started_order_rejects_edits()
    {
        Authenticate();
        var component = await CreateComponent("C-5", 10);
        var board = await CreateBoard(component.Id);
        var input = new OrderInput("Handoff", "Protocol contract", new DateOnly(2026, 9, 24), null,
            [new OrderLineInput(board.Id, 1, 2)]);
        var orderResponse = await _client.PostAsJsonAsync("/api/orders", input);
        var order = (await orderResponse.Content.ReadFromJsonAsync<OrderView>())!;
        var found = await _client.GetFromJsonAsync<List<OrderView>>("/api/orders?q=Protocol");
        Assert.Contains(found!, x => x.Id == order.Id);
        var response = await _client.PostAsync($"/api/orders/{order.Id}/download", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.smt-production.v1+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("SMT-LINE-1", root.GetProperty("destination").GetString());
        Assert.Equal(order.Id.ToString(), root.GetProperty("orderId").GetString());
        Assert.Equal("2026-09-24", root.GetProperty("orderDate").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("productionStartedAtUtc").GetString()));
        var line = root.GetProperty("boards")[0];
        Assert.Equal(board.Id.ToString(), line.GetProperty("boardId").GetString());
        Assert.Equal(1, line.GetProperty("revision").GetInt32());
        Assert.Equal(2, line.GetProperty("buildQuantity").GetInt64());
        Assert.Contains(board.Id.ToString("N"), line.GetProperty("placementProgramId").GetString());
        Assert.Equal(6, root.GetProperty("materials")[0].GetProperty("totalRequired").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict,
            (await _client.PutAsJsonAsync($"/api/orders/{order.Id}", input)).StatusCode);
    }

    [Fact]
    public async Task Order_that_cannot_fit_the_production_batch_is_rejected_before_reservation()
    {
        Authenticate();
        var parts = new List<ComponentView>();
        for (var i = 0; i < 49; i++) parts.Add(await CreateComponent($"BULK-{i}", 10));
        var boardResponse = await _client.PostAsJsonAsync("/api/boards", new BoardInput("BULK-BOARD", "Bulk", "Batch bound",
            100, 50, parts.Select(x => new RecipeInput(x.Id, 1)).ToList()));
        boardResponse.EnsureSuccessStatusCode();
        var board = (await boardResponse.Content.ReadFromJsonAsync<BoardView>())!;
        var response = await _client.PostAsJsonAsync("/api/orders", new OrderInput("Too large", "Must reject early",
            new DateOnly(2026, 9, 24), null, [new(board.Id, 1, 1)]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("batch_limit", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        foreach (var part in parts)
            Assert.Equal(0, (await _client.GetFromJsonAsync<ComponentView>($"/api/components/{part.Id}"))!.ReservedStock);
    }

    [Fact]
    public async Task Competing_stock_reduction_and_reservation_keep_inventory_consistent()
    {
        Authenticate();
        var component = await CreateComponent("C-6", 5);
        var board = await CreateBoard(component.Id);
        var input = new OrderInput("Race", "Stock edit race", new DateOnly(2026, 9, 24), null,
            [new OrderLineInput(board.Id, 1, 1)]);
        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync("/api/orders", input),
            _client.PutAsJsonAsync($"/api/components/{component.Id}",
                new ComponentInput("C-6", "Part", "Demo part", 2)));
        Assert.Single(responses, x => x.IsSuccessStatusCode);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var stock = await _client.GetFromJsonAsync<ComponentView>($"/api/components/{component.Id}");
        Assert.True(stock!.PhysicalStock >= stock.ReservedStock);
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
