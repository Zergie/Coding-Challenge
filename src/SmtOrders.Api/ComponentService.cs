using System.Text.Json;

namespace SmtOrders.Api;

public sealed class ComponentService(Database db, ILogger<ComponentService> log)
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
            .Where(x => CatalogSearch.Match(x.Name, x.Description, query)).OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => x.View()).ToList();

    public async Task<ComponentView> UpdateComponent(Guid id, JsonElement update)
    {
        var result = await db.Write(async changes =>
        {
            var oldRow = await db.Get("C:" + Database.Id(id)) ?? throw Database.Missing("Component");
            var old = Database.Data<ComponentRecord>(oldRow);
            var input = PartialUpdate.Apply(update, new ComponentInput(old.PartNumber, old.Name,
                old.Description, old.PhysicalStock));
            Validate(input);
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

    private async Task<bool> ReferencedByBoard(Guid id) => (await db.List("BR:"))
        .Any(x => Database.Data<BoardRevisionRecord>(x).Recipe.Any(r => r.ComponentId == id));

    private static void Validate(ComponentInput input)
    {
        Database.Required(input.PartNumber, "Part number"); Database.Required(input.Name, "Name");
        Database.Required(input.Description, "Description");
        if (input.PhysicalStock < 0) throw new DomainException(400, "invalid_input", "Physical stock cannot be negative.");
    }
}
