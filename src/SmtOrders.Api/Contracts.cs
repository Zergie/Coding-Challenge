using System.Text.Json.Serialization;

namespace SmtOrders.Api;

public sealed record ComponentInput(string PartNumber, string Name, string Description, long PhysicalStock);
public sealed record ComponentView(Guid Id, string PartNumber, string Name, string Description,
    long PhysicalStock, long ReservedStock, long AvailableStock);
public sealed record RecipeInput(Guid ComponentId, long QuantityPerBoard);
public sealed record BoardInput(string PartNumber, string Name, string Description, decimal LengthMm,
    decimal WidthMm, IReadOnlyList<RecipeInput> Recipe);
public sealed record BoardEdit(string Name, string Description, decimal LengthMm, decimal WidthMm,
    IReadOnlyList<RecipeInput> Recipe);
public sealed record RecipeView(Guid ComponentId, string PartNumber, long QuantityPerBoard);
public sealed record BoardView(Guid Id, string PartNumber, int Revision, string Name, string Description,
    decimal LengthMm, decimal WidthMm, IReadOnlyList<RecipeView> Recipe);
public sealed record OrderLineInput(Guid BoardId, int Revision, long BuildQuantity);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderInput(string Name, string Description, DateOnly OrderDate,
    IReadOnlyList<OrderLineInput> Boards);
public sealed record OrderLineView(Guid BoardId, int Revision, long BuildQuantity);
[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
public enum OrderStatus { Reserved, Started }
public sealed record OrderView(Guid Id, string Name, string Description, DateOnly OrderDate,
    OrderStatus Status, DateTimeOffset? StartedAtUtc, IReadOnlyList<OrderLineView> Boards);

public sealed record HandoffComponent(Guid ComponentId, string PartNumber, long QuantityPerBoard);
public sealed record HandoffBoard(Guid BoardId, string PartNumber, int Revision, decimal LengthMm,
    decimal WidthMm, long BuildQuantity, IReadOnlyList<HandoffComponent> Components);
public sealed record HandoffMaterial(Guid ComponentId, string PartNumber, long TotalRequired);
public sealed record ProductionHandoff(string SchemaVersion, string Destination, Guid OrderId, string OrderName,
    DateOnly OrderDate, DateTimeOffset ProductionStartedAtUtc,
    IReadOnlyList<HandoffBoard> Boards, IReadOnlyList<HandoffMaterial> Materials)
{
    public const string CurrentSchemaVersion = "1.0";
    public const string MediaType = "application/vnd.smt-production.v1+json";
}

public sealed class DomainException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
