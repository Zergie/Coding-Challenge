using System.Text.Json.Serialization;

namespace SmtOrders.Api;

internal sealed record ComponentRecord([property: JsonIgnore] Guid Id, string PartNumber, string Name, string Description,
    long PhysicalStock, long ReservedStock)
{
    public ComponentView View() => new(Id, PartNumber, Name, Description, PhysicalStock, ReservedStock,
        PhysicalStock - ReservedStock);
}

internal sealed record BoardRecord([property: JsonIgnore] Guid Id, string PartNumber, int LatestRevision);
internal sealed record BoardRevisionRecord([property: JsonIgnore] Guid BoardId, [property: JsonIgnore] int Revision, string Name, string Description,
    decimal LengthMm, decimal WidthMm, IReadOnlyList<RecipeInput> Recipe);
internal sealed record OrderRecord([property: JsonIgnore] Guid Id, string Name, string Description, DateOnly OrderDate,
    OrderStatus Status, DateTimeOffset? StartedAtUtc, IReadOnlyList<OrderLineInput> Boards,
    Dictionary<Guid, long> Demand, DateTimeOffset CreatedAtUtc)
{
    public OrderView View() => new(Id, Name, Description, OrderDate, Status, StartedAtUtc,
        Boards.OrderBy(x => x.BoardId).ThenBy(x => x.Revision)
            .Select(x => new OrderLineView(x.BoardId, x.Revision, x.BuildQuantity)).ToList());
}

internal sealed record ProductionSnapshotHeader(string SchemaVersion, string Destination, string OrderName,
    DateOnly OrderDate, DateTimeOffset StartedAtUtc);
