namespace SmtOrders.Api;

public sealed record RecipeDemand(Guid ComponentId, long QuantityPerBoard);
public sealed record BoardDemand(long BuildQuantity, IReadOnlyList<RecipeDemand> Recipe);

public static class MaterialDemand
{
    public static IReadOnlyDictionary<Guid, long> Calculate(IEnumerable<BoardDemand> boards)
    {
        var totals = new Dictionary<Guid, long>();
        foreach (var board in boards)
        {
            if (board.BuildQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(boards));
            foreach (var line in board.Recipe)
            {
                if (line.QuantityPerBoard <= 0) throw new ArgumentOutOfRangeException(nameof(boards));
                totals[line.ComponentId] = checked(totals.GetValueOrDefault(line.ComponentId) +
                    checked(board.BuildQuantity * line.QuantityPerBoard));
            }
        }
        return totals;
    }
}
