using SmtOrders.Api;

namespace SmtOrders.Tests;

public class MaterialDemandTests
{
    [Fact]
    public void Shared_components_are_summed_across_board_lines()
    {
        var componentA = Guid.NewGuid();
        var componentB = Guid.NewGuid();
        var demand = MaterialDemand.Calculate([
            new BoardDemand(2, [new RecipeDemand(componentA, 3), new RecipeDemand(componentB, 1)]),
            new BoardDemand(1, [new RecipeDemand(componentA, 4)])
        ]);

        Assert.Equal(10, demand[componentA]);
        Assert.Equal(2, demand[componentB]);
    }
}
