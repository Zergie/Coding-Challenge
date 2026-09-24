using Npgsql;

namespace SmtOrders.Api;

public sealed class CatalogService(Database db, ILogger<CatalogService> log)
{
    public async Task<ComponentView> CreateComponent(ComponentInput input)
    {
        Validate(input);
        var id = Guid.NewGuid();
        var result = await db.Write(async (c, t) =>
        {
            await using var command = Database.Command(c, t,
                "insert into components(id,part_number,name,description,physical_stock) values (@id,@part,@name,@description,@stock)",
                ("id", id), ("part", input.PartNumber.Trim()), ("name", input.Name.Trim()),
                ("description", input.Description.Trim()), ("stock", input.PhysicalStock));
            await command.ExecuteNonQueryAsync();
            return await GetComponent(c, t, id) ?? throw Database.Missing("Component");
        });
        log.LogInformation("Component {ComponentId} created", id);
        return result;
    }

    public Task<ComponentView?> FindComponent(Guid id) => db.Read(c => GetComponent(c, null, id));

    public Task<IReadOnlyList<ComponentView>> SearchComponents(string? query) => db.Read(async c =>
    {
        var results = new List<ComponentView>();
        await using var command = Database.Command(c, null, """
            select c.id,c.part_number,c.name,c.description,c.physical_stock,
                   coalesce(sum(r.quantity),0)::bigint
            from components c left join reservations r on r.component_id=c.id
            where @query='' or c.name ilike '%' || @query || '%' or c.description ilike '%' || @query || '%'
            group by c.id order by c.name,c.id
            """, ("query", query?.Trim() ?? ""));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) results.Add(Component(reader));
        return (IReadOnlyList<ComponentView>)results;
    });

    public async Task<ComponentView> UpdateComponent(Guid id, ComponentInput input)
    {
        Validate(input);
        var result = await db.Write(async (c, t) =>
        {
            var old = await GetComponent(c, t, id) ?? throw Database.Missing("Component");
            if (input.PhysicalStock < old.ReservedStock)
                throw new DomainException(409, "stock_reserved",
                    $"Component {old.PartNumber} requires {old.ReservedStock} reserved pieces; requested physical stock is {input.PhysicalStock}.");
            if (old.PartNumber != input.PartNumber.Trim())
            {
                await using var check = Database.Command(c, t,
                    "select exists(select 1 from board_recipe where component_id=@id)", ("id", id));
                if ((bool)(await check.ExecuteScalarAsync() ?? false)) throw Database.Referenced("Component part number");
            }
            await using var command = Database.Command(c, t,
                "update components set part_number=@part,name=@name,description=@description,physical_stock=@stock where id=@id",
                ("id", id), ("part", input.PartNumber.Trim()), ("name", input.Name.Trim()),
                ("description", input.Description.Trim()), ("stock", input.PhysicalStock));
            await command.ExecuteNonQueryAsync();
            return await GetComponent(c, t, id) ?? throw Database.Missing("Component");
        });
        log.LogInformation("Component {ComponentId} updated", id);
        return result;
    }

    public async Task DeleteComponent(Guid id)
    {
        await db.Write(async (c, t) =>
        {
            await using (var check = Database.Command(c, t,
                "select exists(select 1 from board_recipe where component_id=@id)", ("id", id)))
                if ((bool)(await check.ExecuteScalarAsync() ?? false)) throw Database.Referenced("Component");
            await using var command = Database.Command(c, t, "delete from components where id=@id", ("id", id));
            if (await command.ExecuteNonQueryAsync() == 0) throw Database.Missing("Component");
            return true;
        });
        log.LogInformation("Component {ComponentId} deleted", id);
    }

    public async Task<BoardView> CreateBoard(BoardInput input)
    {
        Validate(input);
        var id = Guid.NewGuid();
        var result = await db.Write(async (c, t) =>
        {
            await CheckComponents(c, t, input.Recipe);
            await using (var command = Database.Command(c, t,
                "insert into boards(id,part_number) values (@id,@part)", ("id", id), ("part", input.PartNumber.Trim())))
                await command.ExecuteNonQueryAsync();
            await AddRevision(c, t, id, 1, input.Name, input.Description, input.LengthMm, input.WidthMm, input.Recipe);
            return await GetBoard(c, t, id, 1) ?? throw Database.Missing("Board");
        });
        log.LogInformation("Board {BoardId} created", id);
        return result;
    }

    public Task<BoardView?> FindBoard(Guid id, int? revision = null) => db.Read(async c =>
    {
        var selected = revision ?? await LatestRevision(c, null, id);
        return selected == 0 ? null : await GetBoard(c, null, id, selected);
    });

    public Task<IReadOnlyList<BoardView>> SearchBoards(string? query) => db.Read(async c =>
    {
        var ids = new List<(Guid Id, int Revision)>();
        await using (var command = Database.Command(c, null, """
            select b.id,br.revision from boards b join board_revisions br on br.board_id=b.id
            where br.revision=(select max(revision) from board_revisions where board_id=b.id)
              and (@query='' or br.name ilike '%' || @query || '%' or br.description ilike '%' || @query || '%')
            order by br.name,b.id
            """, ("query", query?.Trim() ?? "")))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) ids.Add((reader.GetGuid(0), reader.GetInt32(1)));
        var results = new List<BoardView>();
        foreach (var (id, revision) in ids)
            results.Add((await GetBoard(c, null, id, revision))!);
        return (IReadOnlyList<BoardView>)results;
    });

    public async Task<BoardView> ReviseBoard(Guid id, BoardEdit input)
    {
        Validate(input);
        var result = await db.Write(async (c, t) =>
        {
            var revision = await LatestRevision(c, t, id);
            if (revision == 0) throw Database.Missing("Board");
            await CheckComponents(c, t, input.Recipe);
            await AddRevision(c, t, id, revision + 1, input.Name, input.Description, input.LengthMm, input.WidthMm, input.Recipe);
            return await GetBoard(c, t, id, revision + 1) ?? throw Database.Missing("Board");
        });
        log.LogInformation("Board {BoardId} revised to {Revision}", id, result.Revision);
        return result;
    }

    public async Task DeleteBoard(Guid id)
    {
        await db.Write(async (c, t) =>
        {
            await using (var check = Database.Command(c, t,
                "select exists(select 1 from order_lines where board_id=@id)", ("id", id)))
                if ((bool)(await check.ExecuteScalarAsync() ?? false)) throw Database.Referenced("Board");
            await using var command = Database.Command(c, t, "delete from boards where id=@id", ("id", id));
            if (await command.ExecuteNonQueryAsync() == 0) throw Database.Missing("Board");
            return true;
        });
        log.LogInformation("Board {BoardId} deleted", id);
    }

    private static void Validate(ComponentInput input)
    {
        Database.Required(input.PartNumber, "Part number");
        Database.Required(input.Name, "Name");
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
        Database.Required(input.Name, "Name");
        Database.Required(input.Description, "Description");
        Database.Positive(input.LengthMm, "Length");
        Database.Positive(input.WidthMm, "Width");
        if (input.Recipe is null || input.Recipe.Count == 0)
            throw new DomainException(400, "invalid_input", "A Board needs at least one Component.");
        if (input.Recipe.Select(x => x.ComponentId).Distinct().Count() != input.Recipe.Count)
            throw new DomainException(400, "invalid_input", "Recipe contains a duplicate Component.");
        foreach (var line in input.Recipe) Database.Positive(line.QuantityPerBoard, "Recipe quantity");
    }

    private static async Task CheckComponents(NpgsqlConnection c, NpgsqlTransaction t, IReadOnlyList<RecipeInput> recipe)
    {
        foreach (var line in recipe)
        {
            await using var command = Database.Command(c, t, "select exists(select 1 from components where id=@id)", ("id", line.ComponentId));
            if (!(bool)(await command.ExecuteScalarAsync() ?? false)) throw Database.Missing("Component");
        }
    }

    private static async Task AddRevision(NpgsqlConnection c, NpgsqlTransaction t, Guid id, int revision,
        string name, string description, decimal length, decimal width, IReadOnlyList<RecipeInput> recipe)
    {
        await using (var command = Database.Command(c, t, """
            insert into board_revisions(board_id,revision,name,description,length_mm,width_mm)
            values (@id,@revision,@name,@description,@length,@width)
            """, ("id", id), ("revision", revision), ("name", name.Trim()),
            ("description", description.Trim()), ("length", length), ("width", width)))
            await command.ExecuteNonQueryAsync();
        foreach (var line in recipe)
        {
            await using var command = Database.Command(c, t, """
                insert into board_recipe(board_id,revision,component_id,quantity_per_board)
                values (@id,@revision,@component,@quantity)
                """, ("id", id), ("revision", revision), ("component", line.ComponentId), ("quantity", line.QuantityPerBoard));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> LatestRevision(NpgsqlConnection c, NpgsqlTransaction? t, Guid id)
    {
        await using var command = Database.Command(c, t,
            "select coalesce(max(revision),0) from board_revisions where board_id=@id", ("id", id));
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<BoardView?> GetBoard(NpgsqlConnection c, NpgsqlTransaction? t, Guid id, int revision)
    {
        string? part = null, name = null, description = null;
        decimal length = 0, width = 0;
        await using (var command = Database.Command(c, t, """
            select b.part_number,br.name,br.description,br.length_mm,br.width_mm
            from boards b join board_revisions br on br.board_id=b.id
            where b.id=@id and br.revision=@revision
            """, ("id", id), ("revision", revision)))
        await using (var reader = await command.ExecuteReaderAsync())
            if (await reader.ReadAsync())
            {
                part = reader.GetString(0); name = reader.GetString(1); description = reader.GetString(2);
                length = reader.GetDecimal(3); width = reader.GetDecimal(4);
            }
        if (part is null) return null;
        var recipe = new List<RecipeView>();
        await using (var command = Database.Command(c, t, """
            select r.component_id,c.part_number,r.quantity_per_board
            from board_recipe r join components c on c.id=r.component_id
            where r.board_id=@id and r.revision=@revision order by c.part_number,r.component_id
            """, ("id", id), ("revision", revision)))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) recipe.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2)));
        return new(id, part, revision, name!, description!, length, width, recipe);
    }

    private static async Task<ComponentView?> GetComponent(NpgsqlConnection c, NpgsqlTransaction? t, Guid id)
    {
        await using var command = Database.Command(c, t, """
            select c.id,c.part_number,c.name,c.description,c.physical_stock,
                   coalesce(sum(r.quantity),0)::bigint
            from components c left join reservations r on r.component_id=c.id
            where c.id=@id group by c.id
            """, ("id", id));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Component(reader) : null;
    }

    private static ComponentView Component(NpgsqlDataReader reader)
    {
        var physical = reader.GetInt64(4);
        var reserved = reader.GetInt64(5);
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            physical, reserved, physical - reserved);
    }
}
