using Azure.Data.Tables;
using System.Text.Json;

namespace SmtOrders.Api;

public sealed class OrderService(Database db, IConfiguration configuration, ILogger<OrderService> log)
{
    public async Task<OrderView> Create(OrderInput input)
    {
        Validate(input);
        var id = Guid.NewGuid();
        var result = await db.Write(async changes =>
        {
            var (demand, recipeLines) = await Demand(input.Boards);
            CheckDownloadCapacity(input.Boards.Count, recipeLines, demand.Count);
            await AdjustStock(changes, demand, new Dictionary<Guid, long>());
            var order = MakeOrder(id, input, demand);
            changes.Add(Database.OrderRow(order));
            return order.View();
        });
        log.LogInformation("Order {OrderId} created with reservation", id);
        return result;
    }

    public async Task<OrderView?> Find(Guid id) => await db.Get(Database.OrderKey(id)) is { } row
        ? Database.Order(row).View() : null;

    public async Task<IReadOnlyList<OrderView>> Search(string? query) => (await db.List("O:"))
        .Select(Database.Order)
        .Where(x => string.IsNullOrWhiteSpace(query) || x.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || x.Description.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Select(x => x.View()).ToList();

    public async Task<OrderView> Update(Guid id, JsonElement update)
    {
        var result = await db.Write(async changes =>
        {
            var row = await db.Get(Database.OrderKey(id)) ?? throw Database.Missing("Order");
            var old = Database.Order(row);
            if (old.Status == OrderStatus.Started) throw Started();
            var input = PartialUpdate.Apply(update, new OrderInput(old.Name, old.Description,
                old.OrderDate, old.Boards));
            Validate(input);
            var (demand, recipeLines) = await Demand(input.Boards);
            CheckDownloadCapacity(input.Boards.Count, recipeLines, demand.Count);
            await AdjustStock(changes, demand, old.Demand);
            var updated = MakeOrder(id, input, demand) with { CreatedAtUtc = old.CreatedAtUtc };
            changes.Replace(row, Database.OrderRow(updated));
            return updated.View();
        });
        log.LogInformation("Order {OrderId} edited; reservation replaced", id);
        return result;
    }

    public async Task Delete(Guid id)
    {
        await db.Write(async changes =>
        {
            var row = await db.Get(Database.OrderKey(id)) ?? throw Database.Missing("Order");
            var old = Database.Order(row);
            if (old.Status == OrderStatus.Started) throw Started();
            await AdjustStock(changes, new Dictionary<Guid, long>(), old.Demand);
            changes.Delete(row);
            return true;
        });
        log.LogInformation("Order {OrderId} deleted; reservation released", id);
    }

    public async Task<ProductionHandoff> Download(Guid id)
    {
        var retry = await db.Write(async changes =>
        {
            var row = await db.Get(Database.OrderKey(id)) ?? throw Database.Missing("Order");
            var order = Database.Order(row);
            if (order.Status == OrderStatus.Started) return true;
            var destination = configuration["Production:Destination"];
            Database.Required(destination, "Production destination configuration");
            var started = DateTimeOffset.UtcNow;
            changes.Add(Database.Row(Database.SnapshotHeaderKey(id), new ProductionSnapshotHeader(ProductionHandoff.CurrentSchemaVersion, destination!.Trim(),
                order.Name, order.OrderDate, started)));
            foreach (var line in order.Boards)
            {
                var boardRow = await db.Get(Database.BoardKey(line.BoardId)) ?? throw Database.Missing("Board");
                var board = Database.Board(boardRow);
                var revisionRow = await db.Get(Database.BoardRevisionKey(line.BoardId, line.Revision))
                    ?? throw Database.Missing("Board revision");
                var revision = Database.BoardRevision(revisionRow);
                changes.Add(new TableEntity(Database.Partition, Database.SnapshotBoardKey(id, line.BoardId, line.Revision))
                {
                    ["PartNumber"] = board.PartNumber,
                    ["LengthMilliMm"] = (long)(revision.LengthMm * 1000), ["WidthMilliMm"] = (long)(revision.WidthMm * 1000),
                    ["BuildQuantity"] = line.BuildQuantity
                });
                foreach (var ingredient in revision.Recipe)
                {
                    var componentRow = await db.Get(Database.ComponentKey(ingredient.ComponentId)) ?? throw Database.Missing("Component");
                    var component = Database.Component(componentRow);
                    changes.Add(new TableEntity(Database.Partition,
                        Database.SnapshotComponentKey(id, line.BoardId, line.Revision, ingredient.ComponentId))
                    {
                        ["PartNumber"] = component.PartNumber, ["QuantityPerBoard"] = ingredient.QuantityPerBoard,
                        ["TotalRequired"] = checked(ingredient.QuantityPerBoard * line.BuildQuantity)
                    });
                }
            }
            foreach (var (componentId, required) in order.Demand)
            {
                var componentRow = await db.Get(Database.ComponentKey(componentId)) ?? throw Database.Missing("Component");
                var component = Database.Component(componentRow);
                if (component.ReservedStock < required || component.PhysicalStock < required)
                    throw new InvalidOperationException("Reservation and stock are inconsistent.");
                changes.Replace(componentRow, Database.ComponentRow(
                    component with { PhysicalStock = component.PhysicalStock - required, ReservedStock = component.ReservedStock - required }));
            }
            changes.Replace(row, Database.OrderRow(order with
            {
                Status = OrderStatus.Started, StartedAtUtc = started, Demand = new Dictionary<Guid, long>()
            }));
            return false;
        });
        if (retry) log.LogInformation("Production handoff retry for Order {OrderId}", id);
        else log.LogInformation("Production started for Order {OrderId}", id);
        return await LoadSnapshot(id);
    }

    private async Task<(IReadOnlyDictionary<Guid, long> Totals, int RecipeLines)> Demand(IReadOnlyList<OrderLineInput> lines)
    {
        var boards = new List<BoardDemand>();
        var recipeLines = 0;
        foreach (var line in lines)
        {
            var revisionRow = await db.Get(Database.BoardRevisionKey(line.BoardId, line.Revision));
            if (revisionRow is null || await db.Get(Database.BoardKey(line.BoardId)) is null)
                throw Database.Missing($"Board revision {line.BoardId}/{line.Revision}");
            var revision = Database.BoardRevision(revisionRow);
            recipeLines = checked(recipeLines + revision.Recipe.Count);
            boards.Add(new(line.BuildQuantity, revision.Recipe.Select(x => new RecipeDemand(x.ComponentId, x.QuantityPerBoard)).ToList()));
        }
        try { return (MaterialDemand.Calculate(boards), recipeLines); }
        catch (OverflowException) { throw new DomainException(400, "invalid_input", "Material demand exceeds supported quantity."); }
    }

    private static void CheckDownloadCapacity(int boardLines, int recipeLines, int distinctComponents)
    {
        // Header + Board snapshots + recipe snapshots + stock updates + Order + version.
        if (3L + boardLines + recipeLines + distinctComponents > 100)
            throw new DomainException(400, "batch_limit",
                "This Order needs more than 100 Table Storage operations to start production.");
    }

    private async Task AdjustStock(Database.ChangeSet changes, IReadOnlyDictionary<Guid, long> next,
        IReadOnlyDictionary<Guid, long> old)
    {
        foreach (var componentId in next.Keys.Union(old.Keys))
        {
            var row = await db.Get(Database.ComponentKey(componentId)) ?? throw Database.Missing("Component");
            var component = Database.Component(row);
            var otherReservations = component.ReservedStock - old.GetValueOrDefault(componentId);
            var required = next.GetValueOrDefault(componentId);
            var available = component.PhysicalStock - otherReservations;
            if (required > available)
                throw new DomainException(409, "insufficient_stock",
                    $"Component {component.PartNumber} requires {required} pieces but only {available} are available; shortfall {required - available}.");
            changes.Replace(row, Database.ComponentRow(
                component with { ReservedStock = checked(otherReservations + required) }));
        }
    }

    private async Task<ProductionHandoff> LoadSnapshot(Guid id)
    {
        var headerRow = await db.Get(Database.SnapshotHeaderKey(id)) ?? throw new InvalidOperationException("Started Order has no production snapshot.");
        var header = Database.Data<ProductionSnapshotHeader>(headerRow);
        if (header.SchemaVersion != ProductionHandoff.CurrentSchemaVersion)
            throw new InvalidOperationException($"Unsupported production snapshot version: {header.SchemaVersion}.");
        var boards = new List<SnapshotBoard>();
        foreach (var boardRow in await db.List(Database.SnapshotBoardPrefix(id)))
        {
            var (boardId, revision) = Database.SnapshotBoard(boardRow, id);
            var components = new List<SnapshotComponent>();
            foreach (var componentRow in await db.List(Database.SnapshotComponentPrefix(boardRow, id)))
            {
                var componentId = Database.SnapshotComponentId(componentRow, boardRow, id);
                components.Add(new(componentId, (string)componentRow["PartNumber"],
                    (long)componentRow["QuantityPerBoard"], (long)componentRow["TotalRequired"]));
            }
            boards.Add(new(boardId, (string)boardRow["PartNumber"], revision,
                (long)boardRow["LengthMilliMm"] / 1000m, (long)boardRow["WidthMilliMm"] / 1000m,
                (long)boardRow["BuildQuantity"], components.OrderBy(x => x.PartNumber).ToList()));
        }
        var orderedBoards = boards.OrderBy(x => x.BoardId).ThenBy(x => x.Revision).ToList();
        var allComponents = orderedBoards.SelectMany(x => x.Components).ToList();
        var currentBoards = orderedBoards.Select(board => new HandoffBoard(board.BoardId,
            board.PartNumber, board.Revision, board.LengthMm, board.WidthMm, board.BuildQuantity,
            board.Components.Select(component => new HandoffComponent(component.ComponentId,
                component.PartNumber, component.QuantityPerBoard)).ToList())).ToList();
        var currentMaterials = allComponents.GroupBy(x => x.ComponentId)
            .Select(group => new HandoffMaterial(group.Key, group.First().PartNumber, SumRequired(group)))
            .OrderBy(x => x.PartNumber).ThenBy(x => x.ComponentId).ToList();
        return new(ProductionHandoff.CurrentSchemaVersion, header.Destination, id, header.OrderName, header.OrderDate,
            header.StartedAtUtc, currentBoards, currentMaterials);
    }

    private sealed record SnapshotComponent(Guid ComponentId, string PartNumber, long QuantityPerBoard, long TotalRequired);
    private sealed record SnapshotBoard(Guid BoardId, string PartNumber, int Revision, decimal LengthMm,
        decimal WidthMm, long BuildQuantity, IReadOnlyList<SnapshotComponent> Components);

    private static long SumRequired(IEnumerable<SnapshotComponent> components) =>
        components.Aggregate(0L, (total, component) => checked(total + component.TotalRequired));

    private static OrderRecord MakeOrder(Guid id, OrderInput input, IReadOnlyDictionary<Guid, long> demand) =>
        new(id, input.Name.Trim(), input.Description.Trim(), input.OrderDate,
            OrderStatus.Reserved, null, input.Boards, demand.ToDictionary(), DateTimeOffset.UtcNow);

    private static void Validate(OrderInput input)
    {
        Database.Required(input.Name, "Name"); Database.Required(input.Description, "Description");
        if (input.OrderDate == default) throw new DomainException(400, "invalid_input", "Order date is required.");
        if (input.Boards is null || input.Boards.Count == 0)
            throw new DomainException(400, "invalid_input", "An Order needs at least one Board.");
        if (input.Boards.Select(x => (x.BoardId, x.Revision)).Distinct().Count() != input.Boards.Count)
            throw new DomainException(400, "invalid_input", "Order contains a duplicate Board revision.");
        foreach (var line in input.Boards)
        {
            Database.Positive(line.BuildQuantity, "Build quantity");
            if (line.Revision <= 0) throw new DomainException(400, "invalid_input", "Board revision must be positive.");
        }
    }
    private static DomainException Started() => new(409, "order_started", "A started Order cannot be edited or deleted.");
}
