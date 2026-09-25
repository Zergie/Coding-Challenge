using System.Text.Json;

namespace SmtOrders.Api;

public sealed class BoardService(Database db, ILogger<BoardService> log)
{
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
            changes.Add(Database.BoardRow(board));
            changes.Add(Database.Row(index, id));
            changes.Add(Database.BoardRevisionRow(revision));
            return await View(board, revision);
        });
        log.LogInformation("Board {BoardId} created", id);
        return result;
    }

    public Task<BoardView?> FindBoard(Guid id, int? revision = null) => db.ReadStable(async () =>
    {
        if (await db.Get(Database.BoardKey(id)) is not { } boardRow) return null;
        var board = Database.Board(boardRow);
        if (await db.Get(Database.BoardRevisionKey(id, revision ?? board.LatestRevision)) is not { } revisionRow) return null;
        return await View(board, Database.BoardRevision(revisionRow));
    });

    public Task<IReadOnlyList<BoardView>?> ListBoardRevisions(Guid id) => db.ReadStable<IReadOnlyList<BoardView>?>(async () =>
    {
        if (await db.Get(Database.BoardKey(id)) is not { } boardRow) return null;
        var board = Database.Board(boardRow);
        var revisions = new List<BoardView>();
        foreach (var row in await db.List(Database.BoardRevisionPrefix(id)))
            revisions.Add(await View(board, Database.BoardRevision(row)));
        if (revisions.Count != board.LatestRevision) throw new StaleReadException();
        return revisions.OrderBy(x => x.Revision).ToList();
    });

    public Task<IReadOnlyList<BoardView>> SearchBoards(string? query) => db.ReadStable<IReadOnlyList<BoardView>>(async () =>
    {
        var results = new List<BoardView>();
        foreach (var row in await db.List("B:"))
        {
            var board = Database.Board(row);
            var revisionRow = await db.Get(Database.BoardRevisionKey(board.Id, board.LatestRevision)) ?? throw new StaleReadException();
            var revision = Database.BoardRevision(revisionRow);
            if (CatalogSearch.Match(revision.Name, revision.Description, query)) results.Add(await View(board, revision));
        }
        return results.OrderBy(x => x.Name).ThenBy(x => x.Id).ToList();
    });

    public async Task<BoardView> ReviseBoard(Guid id, JsonElement update)
    {
        var result = await db.Write(async changes =>
        {
            var row = await db.Get(Database.BoardKey(id)) ?? throw Database.Missing("Board");
            var board = Database.Board(row);
            if (board.LatestRevision >= 97)
                throw new DomainException(400, "revision_limit", "A Board supports at most 97 revisions in this demo.");
            var latestRow = await db.Get(Database.BoardRevisionKey(id, board.LatestRevision)) ?? throw new StaleReadException();
            var latest = Database.BoardRevision(latestRow);
            var input = PartialUpdate.Apply(update, new BoardEdit(latest.Name, latest.Description,
                latest.LengthMm, latest.WidthMm, latest.Recipe));
            Validate(input);
            await CheckComponents(input.Recipe);
            var revision = MakeRevision(id, checked(board.LatestRevision + 1), input);
            changes.Replace(row, Database.BoardRow(board with { LatestRevision = revision.Revision }));
            changes.Add(Database.BoardRevisionRow(revision));
            return await View(board, revision);
        });
        log.LogInformation("Board {BoardId} revised to {Revision}", id, result.Revision);
        return result;
    }

    public async Task DeleteBoard(Guid id)
    {
        await db.Write(async changes =>
        {
            var row = await db.Get(Database.BoardKey(id)) ?? throw Database.Missing("Board");
            foreach (var orderRow in await db.List("O:"))
                if (Database.Order(orderRow).Boards.Any(x => x.BoardId == id)) throw Database.Referenced("Board");
            var board = Database.Board(row);
            changes.Delete(row);
            changes.Delete((await db.Get(Database.PartKey("BP:", board.PartNumber)))!);
            foreach (var revision in await db.List(Database.BoardRevisionPrefix(id))) changes.Delete(revision);
            return true;
        });
        log.LogInformation("Board {BoardId} deleted", id);
    }

    private static BoardRevisionRecord MakeRevision(Guid id, int revision, BoardEdit input) =>
        new(id, revision, input.Name.Trim(), input.Description.Trim(), input.LengthMm, input.WidthMm, input.Recipe);

    private async Task<BoardView> View(BoardRecord board, BoardRevisionRecord revision)
    {
        var recipe = new List<RecipeView>();
        foreach (var line in revision.Recipe)
        {
            var componentRow = await db.Get(Database.ComponentKey(line.ComponentId)) ?? throw new StaleReadException();
            var component = Database.Component(componentRow);
            recipe.Add(new(line.ComponentId, component.PartNumber, line.QuantityPerBoard));
        }
        return new(board.Id, board.PartNumber, revision.Revision, revision.Name, revision.Description,
            revision.LengthMm, revision.WidthMm, recipe.OrderBy(x => x.PartNumber).ThenBy(x => x.ComponentId).ToList());
    }

    private async Task CheckComponents(IReadOnlyList<RecipeInput> recipe)
    {
        foreach (var line in recipe)
            if (await db.Get(Database.ComponentKey(line.ComponentId)) is null) throw Database.Missing("Component");
    }

    internal async Task<bool> ReferencesComponent(Guid id) => (await db.List("BR:"))
        .Any(row => Database.BoardRevision(row).Recipe.Any(line => line.ComponentId == id));

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
