using Azure.Data.Tables;

namespace SmtOrders.Api;

public static class DemoSeed
{
    public static async Task Reset(IServiceProvider services)
    {
        var table = services.GetRequiredService<TableClient>();
        await table.DeleteAsync();
        await services.GetRequiredService<Database>().Initialize();
        using var scope = services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogService>();
        var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
        var resistor = await catalog.CreateComponent(new("RES-10K", "10 kΩ resistor", "0603 resistor", 1000));
        var chip = await catalog.CreateComponent(new("MCU-01", "Microcontroller", "QFN controller", 100));
        await catalog.CreateComponent(new("SPARE-01", "Spare capacitor", "Unassigned component", 50));
        var board = await catalog.CreateBoard(new("CTRL-BOARD", "Controller board", "Demo board", 100, 50,
            [new(resistor.Id, 3), new(chip.Id, 1)]));
        var order = await orders.Create(new("Demo order", "Ready for production", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new(board.Id, 1, 10)]));
        Console.WriteLine($"Demo reset complete. Board: {board.Id}; reserved Order: {order.Id}");
    }
}
