using Azure.Data.Tables;

namespace SmtOrders.Api;

public sealed class CatalogService(Database db, ILogger<CatalogService> log)
{
    public async Task<ComponentView> CreateComponent(ComponentInput input)
    {
        Validate(input);
        var id = Guid.NewGuid();
        var part = input.PartNumber.Trim();
        var result = await db.Write(async changes =>
        {
            var indexKey = Database.PartKey("CP:", part);
            if (await db.Get(indexKey) is not null) throw Database.Duplicate();
            var record = new ComponentRecord(id, part, input.Name.Trim(), input.Description.Trim(), input.PhysicalStock, 0);
            changes.Add(Database.Row("C:" + Database.Id(id), record));
            changes.Add(Database.Row(indexKey, id));
            return record.View();
        });
        log.LogInformation("Component {ComponentId} created", id);
        return result;
    }

    public async Task<ComponentView?> FindComponent(Guid id) =>
        await db.Get("C:" + Database.Id(id)) is { } row ? Database.Data<ComponentRecord>(row).View() : null;

    public async Task<IReadOnlyList<ComponentView>> SearchComponents(string? query) =>
        (await db.List("C:")).Select(Database.Data<ComponentRecord>)
            .Where(x => Match(x.Name, x.Description, query)).OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => x.View()).ToList();

    public async Task<ComponentView> UpdateComponent(Guid id, ComponentInput input)
    {
        Validate(input);
        var result = await db.Write(async changes =>
        {
            var oldRow = await db.Get("C:" + Database.Id(id)) ?? throw Database.Missing("Component");
            var old = Database.Data<ComponentRecord>(oldRow);
            if (input.PhysicalStock < old.ReservedStock)
                throw new DomainException(409, "stock_reserved",
                    $"Component {old.PartNumber} requires {old.ReservedStock} reserved pieces; requested physical stock is {input.PhysicalStock}.");
            var part = input.PartNumber.Trim();
            if (part != old.PartNumber)
            {
                if (await ReferencedByBoard(id)) throw Database.Referenced("Component part number");
                var newIndex = Database.PartKey("CP:", part);
                if (newIndex != Database.PartKey("CP:", old.PartNumber))
                {
                    if (await db.Get(newIndex) is not null) throw Database.Duplicate();
                    changes.Delete((await db.Get(Database.PartKey("CP:", old.PartNumber)))!);
                    changes.Add(Database.Row(newIndex, id));
                }
            }
            var updated = old with { PartNumber = part, Name = input.Name.Trim(), Description = input.Description.Trim(),
                PhysicalStock = input.PhysicalStock };
            changes.Replace(oldRow, Database.Row(oldRow.RowKey, updated));
            return updated.View();
        });
        log.LogInformation("Component {ComponentId} updated", id);
        return result;
    }

    public async Task DeleteComponent(Guid id)
    {
        await db.Write(async changes =>
        {
            var row = await db.Get("C:" + Database.Id(id)) ?? throw Database.Missing("Component");
            if (await ReferencedByBoard(id)) throw Database.Referenced("Component");
            var component = Database.Data<ComponentRecord>(row);
            if (component.ReservedStock != 0) throw Database.Referenced("Component");
            changes.Delete(row);
            changes.Delete((await db.Get(Database.PartKey("CP:", component.PartNumber)))!);
            return true;
        });
        log.LogInformation("Component {ComponentId} deleted", id);
    }

    public async Task<BoardView> CreateBoard(BoardInput input)
    {
        Validate(input);
        var id = Guid.NewGuid();
        var part = input.PartNumber.Trim();
        var result = await db.Write(async changes =>
        {
            await CheckComponents(input.Recipe);
            var index = Database.PartKey("BP:", part);
            if (await db.Get(index) is not null) throw Database.Duplicate();
            var board = new BoardRecord(id, part, 1);
            var revision = MakeRevision(id, 1, new(input.Name, input.Description, input.LengthMm, input.WidthMm, input.Recipe));
            changes.Add(Database.Row("B:" + Database.Id(id), board));
            changes.Add(Database.Row(index, id));
            changes.Add(Database.Row(RevisionKey(id, 1), revision));
            return await View(board, revision);
        });
        log.LogInformation("Board {BoardId} created", id);
        return result;
    }

    public Task<BoardView?> FindBoard(Guid id, int? revision = null) => db.ReadStable(async () =>
    {
        if (await db.Get("B:" + Database.Id(id)) is not { } boardRow) return null;
        var board = Database.Data<BoardRecord>(boardRow);
        if (await db.Get(RevisionKey(id, revision ?? board.LatestRevision)) is not { } revisionRow) return null;
        return await View(board, Database.Data<BoardRevisionRecord>(revisionRow));
    });

    public Task<IReadOnlyList<BoardView>> SearchBoards(string? query) => db.ReadStable<IReadOnlyList<BoardView>>(async () =>
    {
        var results = new List<BoardView>();
        foreach (var row in await db.List("B:"))
        {
            var board = Database.Data<BoardRecord>(row);
            var revisionRow = await db.Get(RevisionKey(board.Id, board.LatestRevision)) ?? throw new StaleReadException();
            var revision = Database.Data<BoardRevisionRecord>(revisionRow);
            if (Match(revision.Name, revision.Description, query)) results.Add(await View(board, revision));
        }
        return results.OrderBy(x => x.Name).ThenBy(x => x.Id).ToList();
    });

    public async Task<BoardView> ReviseBoard(Guid id, BoardEdit input)
    {
        Validate(input);
        var result = await db.Write(async changes =>
        {
            var row = await db.Get("B:" + Database.Id(id)) ?? throw Database.Missing("Board");
            var board = Database.Data<BoardRecord>(row);
            if (board.LatestRevision >= 97)
                throw new DomainException(400, "revision_limit", "A Board supports at most 97 revisions in this demo.");
            await CheckComponents(input.Recipe);
            var revision = MakeRevision(id, checked(board.LatestRevision + 1), input);
            changes.Replace(row, Database.Row(row.RowKey, board with { LatestRevision = revision.Revision }));
            changes.Add(Database.Row(RevisionKey(id, revision.Revision), revision));
            return await View(board, revision);
        });
        log.LogInformation("Board {BoardId} revised to {Revision}", id, result.Revision);
        return result;
    }

    public async Task DeleteBoard(Guid id)
    {
        await db.Write(async changes =>
        {
            var row = await db.Get("B:" + Database.Id(id)) ?? throw Database.Missing("Board");
            foreach (var orderRow in await db.List("O:"))
                if (Database.Data<OrderRecord>(orderRow).Boards.Any(x => x.BoardId == id)) throw Database.Referenced("Board");
            var board = Database.Data<BoardRecord>(row);
            changes.Delete(row);
            changes.Delete((await db.Get(Database.PartKey("BP:", board.PartNumber)))!);
            foreach (var revision in await db.List("BR:" + Database.Id(id) + ":")) changes.Delete(revision);
            return true;
        });
        log.LogInformation("Board {BoardId} deleted", id);
    }

    internal static string RevisionKey(Guid id, int revision) => $"BR:{Database.Id(id)}:{revision:D8}";
    private static BoardRevisionRecord MakeRevision(Guid id, int revision, BoardEdit input) =>
        new(id, revision, input.Name.Trim(), input.Description.Trim(), input.LengthMm, input.WidthMm, input.Recipe);

    private async Task<BoardView> View(BoardRecord board, BoardRevisionRecord revision)
    {
        var recipe = new List<RecipeView>();
        foreach (var line in revision.Recipe)
        {
            var componentRow = await db.Get("C:" + Database.Id(line.ComponentId)) ?? throw new StaleReadException();
            var component = Database.Data<ComponentRecord>(componentRow);
            recipe.Add(new(line.ComponentId, component.PartNumber, line.QuantityPerBoard));
        }
        return new(board.Id, board.PartNumber, revision.Revision, revision.Name, revision.Description,
            revision.LengthMm, revision.WidthMm, recipe.OrderBy(x => x.PartNumber).ThenBy(x => x.ComponentId).ToList());
    }

    private async Task CheckComponents(IReadOnlyList<RecipeInput> recipe)
    {
        foreach (var line in recipe)
            if (await db.Get("C:" + Database.Id(line.ComponentId)) is null) throw Database.Missing("Component");
    }

    private async Task<bool> ReferencedByBoard(Guid id) => (await db.List("BR:"))
        .Any(x => Database.Data<BoardRevisionRecord>(x).Recipe.Any(r => r.ComponentId == id));

    private static bool Match(string name, string description, string? query) => string.IsNullOrWhiteSpace(query) ||
        name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
        description.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void Validate(ComponentInput input)
    {
        Database.Required(input.PartNumber, "Part number"); Database.Required(input.Name, "Name");
        Database.Required(input.Description, "Description");
        if (input.PhysicalStock < 0) throw new DomainException(400, "invalid_input", "Physical stock cannot be negative.");
    }
    private static void Validate(BoardInput input)
    {
        Database.Required(input.PartNumber, "Part number");
        Validate(new BoardEdit(input.Name, input.Description, input.LengthMm, input.WidthMm, input.Recipe));
    }
    private static void Validate(BoardEdit input)
    {
        Database.Required(input.Name, "Name"); Database.Required(input.Description, "Description");
        Database.Positive(input.LengthMm, "Length"); Database.Positive(input.WidthMm, "Width");
        if (input.Recipe is null || input.Recipe.Count == 0)
            throw new DomainException(400, "invalid_input", "A Board needs at least one Component.");
        if (input.Recipe.Select(x => x.ComponentId).Distinct().Count() != input.Recipe.Count)
            throw new DomainException(400, "invalid_input", "Recipe contains a duplicate Component.");
        foreach (var line in input.Recipe) Database.Positive(line.QuantityPerBoard, "Recipe quantity");
    }
}
