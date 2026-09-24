using Npgsql;

namespace SmtOrders.Api;

public sealed class OrderService(Database db, IConfiguration configuration, ILogger<OrderService> log)
{
    public async Task<OrderView> Create(OrderInput input)
    {
        Validate(input);
        var id = Guid.NewGuid();
        var result = await db.Write(async (c, t) =>
        {
            var demand = await Demand(c, t, input.Boards);
            await CheckStock(c, t, demand, null);
            await using (var command = Database.Command(c, t, """
                insert into orders(id,name,description,order_date,due_date,status)
                values (@id,@name,@description,@date,@due,'Reserved')
                """, ("id", id), ("name", input.Name.Trim()), ("description", input.Description.Trim()),
                ("date", input.OrderDate), ("due", input.DueDate)))
                await command.ExecuteNonQueryAsync();
            await SaveLinesAndReservations(c, t, id, input.Boards, demand);
            return await GetOrder(c, t, id) ?? throw Database.Missing("Order");
        });
        log.LogInformation("Order {OrderId} created with reservation", id);
        return result;
    }

    public Task<OrderView?> Find(Guid id) => db.Read(c => GetOrder(c, null, id));

    public Task<IReadOnlyList<OrderView>> Search(string? query) => db.Read(async c =>
    {
        var ids = new List<Guid>();
        await using (var command = Database.Command(c, null, """
            select id from orders where @query='' or name ilike '%' || @query || '%'
              or description ilike '%' || @query || '%' order by created_at,id
            """, ("query", query?.Trim() ?? "")))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        var results = new List<OrderView>();
        foreach (var id in ids) results.Add((await GetOrder(c, null, id))!);
        return (IReadOnlyList<OrderView>)results;
    });

    public async Task<OrderView> Update(Guid id, OrderInput input)
    {
        Validate(input);
        var result = await db.Write(async (c, t) =>
        {
            var old = await GetOrder(c, t, id) ?? throw Database.Missing("Order");
            if (old.Status == "Started") throw Started();
            var demand = await Demand(c, t, input.Boards);
            await CheckStock(c, t, demand, id);
            await using (var command = Database.Command(c, t, """
                update orders set name=@name,description=@description,order_date=@date,due_date=@due where id=@id
                """, ("id", id), ("name", input.Name.Trim()), ("description", input.Description.Trim()),
                ("date", input.OrderDate), ("due", input.DueDate)))
                await command.ExecuteNonQueryAsync();
            await using (var command = Database.Command(c, t, "delete from reservations where order_id=@id", ("id", id)))
                await command.ExecuteNonQueryAsync();
            await using (var command = Database.Command(c, t, "delete from order_lines where order_id=@id", ("id", id)))
                await command.ExecuteNonQueryAsync();
            await SaveLinesAndReservations(c, t, id, input.Boards, demand);
            return await GetOrder(c, t, id) ?? throw Database.Missing("Order");
        });
        log.LogInformation("Order {OrderId} edited; reservation replaced", id);
        return result;
    }

    public async Task Delete(Guid id)
    {
        await db.Write(async (c, t) =>
        {
            var old = await GetOrder(c, t, id) ?? throw Database.Missing("Order");
            if (old.Status == "Started") throw Started();
            await using var command = Database.Command(c, t, "delete from orders where id=@id", ("id", id));
            await command.ExecuteNonQueryAsync();
            return true;
        });
        log.LogInformation("Order {OrderId} deleted; reservation released", id);
    }

    public async Task<ProductionHandoff> Download(Guid id)
    {
        var (handoff, retry) = await db.Write(async (c, t) =>
        {
            var order = await GetOrder(c, t, id) ?? throw Database.Missing("Order");
            if (order.Status == "Started") return (await LoadSnapshot(c, t, id), true);
            var destination = configuration["Production:Destination"];
            Database.Required(destination, "Production destination configuration");
            var started = DateTimeOffset.UtcNow;
            await using (var command = Database.Command(c, t, """
                insert into production_snapshots(order_id,schema_version,destination,order_name,order_date,due_date,started_at_utc)
                values (@id,'1.0',@destination,@name,@date,@due,@started)
                """, ("id", id), ("destination", destination), ("name", order.Name),
                ("date", order.OrderDate), ("due", order.DueDate), ("started", started)))
                await command.ExecuteNonQueryAsync();
            foreach (var line in order.Boards)
                await SnapshotBoard(c, t, id, line);
            await using (var command = Database.Command(c, t, """
                update components c set physical_stock=c.physical_stock-r.quantity
                from reservations r where r.order_id=@id and c.id=r.component_id
                """, ("id", id)))
                await command.ExecuteNonQueryAsync();
            await using (var command = Database.Command(c, t, "delete from reservations where order_id=@id", ("id", id)))
                await command.ExecuteNonQueryAsync();
            await using (var command = Database.Command(c, t,
                "update orders set status='Started',started_at_utc=@started where id=@id",
                ("id", id), ("started", started)))
                await command.ExecuteNonQueryAsync();
            return (await LoadSnapshot(c, t, id), false);
        });
        if (retry) log.LogInformation("Production handoff retry for Order {OrderId}", id);
        else log.LogInformation("Production started for Order {OrderId}", id);
        return handoff;
    }

    private static async Task<IReadOnlyDictionary<Guid, long>> Demand(NpgsqlConnection c, NpgsqlTransaction t,
        IReadOnlyList<OrderLineInput> lines)
    {
        var boards = new List<BoardDemand>();
        foreach (var line in lines)
        {
            var recipe = new List<RecipeDemand>();
            await using (var command = Database.Command(c, t, """
                select component_id,quantity_per_board from board_recipe
                where board_id=@board and revision=@revision
                """, ("board", line.BoardId), ("revision", line.Revision)))
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) recipe.Add(new(reader.GetGuid(0), reader.GetInt64(1)));
            if (recipe.Count == 0) throw Database.Missing($"Board revision {line.BoardId}/{line.Revision}");
            boards.Add(new(line.BuildQuantity, recipe));
        }
        try { return MaterialDemand.Calculate(boards); }
        catch (OverflowException) { throw new DomainException(400, "invalid_input", "Material demand exceeds supported quantity."); }
    }

    private static async Task CheckStock(NpgsqlConnection c, NpgsqlTransaction t,
        IReadOnlyDictionary<Guid, long> demand, Guid? excludingOrder)
    {
        foreach (var (component, required) in demand)
        {
            await using var command = Database.Command(c, t, """
                select c.part_number,c.physical_stock-coalesce(sum(r.quantity),0)::bigint
                from components c left join reservations r
                  on r.component_id=c.id and r.order_id<>@excluding
                where c.id=@component group by c.id
                """, ("component", component), ("excluding", excludingOrder ?? Guid.Empty));
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw Database.Missing("Component");
            var part = reader.GetString(0);
            var available = reader.GetInt64(1);
            if (required > available)
                throw new DomainException(409, "insufficient_stock",
                    $"Component {part} requires {required} pieces but only {available} are available; shortfall {required - available}.");
        }
    }

    private static async Task SaveLinesAndReservations(NpgsqlConnection c, NpgsqlTransaction t, Guid id,
        IReadOnlyList<OrderLineInput> lines, IReadOnlyDictionary<Guid, long> demand)
    {
        foreach (var line in lines)
        {
            await using var command = Database.Command(c, t, """
                insert into order_lines(order_id,board_id,revision,build_quantity)
                values (@order,@board,@revision,@quantity)
                """, ("order", id), ("board", line.BoardId), ("revision", line.Revision), ("quantity", line.BuildQuantity));
            await command.ExecuteNonQueryAsync();
        }
        foreach (var (component, quantity) in demand)
        {
            await using var command = Database.Command(c, t, """
                insert into reservations(order_id,component_id,quantity) values (@order,@component,@quantity)
                """, ("order", id), ("component", component), ("quantity", quantity));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<OrderView?> GetOrder(NpgsqlConnection c, NpgsqlTransaction? t, Guid id)
    {
        string? name = null, description = null, status = null;
        DateOnly date = default;
        DateOnly? due = null;
        DateTimeOffset? started = null;
        await using (var command = Database.Command(c, t, """
            select name,description,order_date,due_date,status,started_at_utc from orders where id=@id
            """, ("id", id)))
        await using (var reader = await command.ExecuteReaderAsync())
            if (await reader.ReadAsync())
            {
                name = reader.GetString(0); description = reader.GetString(1);
                date = reader.GetFieldValue<DateOnly>(2);
                due = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3);
                status = reader.GetString(4);
                started = reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero);
            }
        if (name is null) return null;
        var lines = new List<OrderLineView>();
        await using (var command = Database.Command(c, t, """
            select board_id,revision,build_quantity from order_lines where order_id=@id order by board_id
            """, ("id", id)))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) lines.Add(new(reader.GetGuid(0), reader.GetInt32(1), reader.GetInt64(2)));
        return new(id, name, description!, date, due, status!, started, lines);
    }

    private static async Task SnapshotBoard(NpgsqlConnection c, NpgsqlTransaction t, Guid orderId, OrderLineView line)
    {
        await using (var command = Database.Command(c, t, """
            insert into snapshot_boards(order_id,board_id,part_number,revision,length_mm,width_mm,build_quantity)
            select @order,b.id,b.part_number,br.revision,br.length_mm,br.width_mm,@quantity
            from boards b join board_revisions br on br.board_id=b.id
            where b.id=@board and br.revision=@revision
            """, ("order", orderId), ("board", line.BoardId), ("revision", line.Revision),
            ("quantity", line.BuildQuantity)))
            await command.ExecuteNonQueryAsync();
        await using var components = Database.Command(c, t, """
            insert into snapshot_components(order_id,board_id,component_id,part_number,quantity_per_board,total_required)
            select @order,r.board_id,c.id,c.part_number,r.quantity_per_board,r.quantity_per_board*@quantity
            from board_recipe r join components c on c.id=r.component_id
            where r.board_id=@board and r.revision=@revision
            """, ("order", orderId), ("board", line.BoardId), ("revision", line.Revision),
            ("quantity", line.BuildQuantity));
        await components.ExecuteNonQueryAsync();
    }

    private static async Task<ProductionHandoff> LoadSnapshot(NpgsqlConnection c, NpgsqlTransaction t, Guid id)
    {
        string version, destination, name;
        DateOnly date;
        DateOnly? due;
        DateTimeOffset started;
        await using (var command = Database.Command(c, t, """
            select schema_version,destination,order_name,order_date,due_date,started_at_utc
            from production_snapshots where order_id=@id
            """, ("id", id)))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Started Order has no production snapshot.");
            version = reader.GetString(0); destination = reader.GetString(1); name = reader.GetString(2);
            date = reader.GetFieldValue<DateOnly>(3);
            due = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4);
            started = new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero);
        }
        var boards = new List<HandoffBoard>();
        var boardRows = new List<(Guid Id, string Part, int Revision, decimal Length, decimal Width, long Quantity)>();
        await using (var command = Database.Command(c, t, """
            select board_id,part_number,revision,length_mm,width_mm,build_quantity
            from snapshot_boards where order_id=@id order by board_id
            """, ("id", id)))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                boardRows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetDecimal(3), reader.GetDecimal(4), reader.GetInt64(5)));
        foreach (var board in boardRows)
        {
            var components = new List<HandoffComponent>();
            await using (var command = Database.Command(c, t, """
                select part_number,quantity_per_board,total_required from snapshot_components
                where order_id=@order and board_id=@board order by part_number,component_id
                """, ("order", id), ("board", board.Id)))
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    components.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            boards.Add(new(board.Id, board.Part, board.Revision, board.Length, board.Width,
                board.Quantity, $"{board.Id:N}/r{board.Revision}", components));
        }
        var materials = new List<HandoffMaterial>();
        await using (var command = Database.Command(c, t, """
            select part_number,sum(total_required)::bigint from snapshot_components
            where order_id=@id group by part_number order by part_number
            """, ("id", id)))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) materials.Add(new(reader.GetString(0), reader.GetInt64(1)));
        return new(version, destination, id, name, date, due, started, boards, materials);
    }

    private static void Validate(OrderInput input)
    {
        Database.Required(input.Name, "Name");
        Database.Required(input.Description, "Description");
        if (input.OrderDate == default) throw new DomainException(400, "invalid_input", "Order date is required.");
        if (input.DueDate < input.OrderDate) throw new DomainException(400, "invalid_input", "Due date precedes Order date.");
        if (input.Boards is null || input.Boards.Count == 0)
            throw new DomainException(400, "invalid_input", "An Order needs at least one Board.");
        if (input.Boards.Select(x => x.BoardId).Distinct().Count() != input.Boards.Count)
            throw new DomainException(400, "invalid_input", "Order contains a duplicate Board.");
        foreach (var line in input.Boards)
        {
            Database.Positive(line.BuildQuantity, "Build quantity");
            if (line.Revision <= 0) throw new DomainException(400, "invalid_input", "Board revision must be positive.");
        }
    }

    private static DomainException Started() => new(409, "order_started", "A started Order cannot be edited or deleted.");
}
