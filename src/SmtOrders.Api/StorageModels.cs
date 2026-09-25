namespace SmtOrders.Api;

internal sealed record ComponentRecord(Guid Id, string PartNumber, string Name, string Description,
    long PhysicalStock, long ReservedStock)
{
    public ComponentView View() => new(Id, PartNumber, Name, Description, PhysicalStock, ReservedStock,
        PhysicalStock - ReservedStock);
}

internal sealed record BoardRecord(Guid Id, string PartNumber, int LatestRevision);
internal sealed record BoardRevisionRecord(Guid BoardId, int Revision, string Name, string Description,
    decimal LengthMm, decimal WidthMm, IReadOnlyList<RecipeInput> Recipe);
internal sealed record OrderRecord(Guid Id, string Name, string Description, DateOnly OrderDate, DateOnly? DueDate,
    string Status, DateTimeOffset? StartedAtUtc, IReadOnlyList<OrderLineInput> Boards,
    Dictionary<Guid, long> Demand, DateTimeOffset CreatedAtUtc)
{
    public OrderView View() => new(Id, Name, Description, OrderDate, DueDate, Status, StartedAtUtc,
        Boards.OrderBy(x => x.BoardId).ThenBy(x => x.Revision)
            .Select(x => new OrderLineView(x.BoardId, x.Revision, x.BuildQuantity)).ToList());
}
