using Npgsql;

namespace SmtOrders.Api;

public static class Schema
{
    public static async Task Apply(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(Sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private const string Sql = """
        create table if not exists components (
          id uuid primary key, part_number text not null unique, name text not null,
          description text not null, physical_stock bigint not null check (physical_stock >= 0),
          created_at timestamptz not null default now()
        );
        create table if not exists boards (
          id uuid primary key, part_number text not null unique,
          created_at timestamptz not null default now()
        );
        create table if not exists board_revisions (
          board_id uuid not null references boards(id) on delete cascade,
          revision integer not null check (revision > 0),
          name text not null, description text not null,
          length_mm numeric(12,3) not null check (length_mm > 0),
          width_mm numeric(12,3) not null check (width_mm > 0),
          primary key (board_id, revision)
        );
        create table if not exists board_recipe (
          board_id uuid not null, revision integer not null,
          component_id uuid not null references components(id) on delete restrict,
          quantity_per_board bigint not null check (quantity_per_board > 0),
          primary key (board_id, revision, component_id),
          foreign key (board_id, revision) references board_revisions(board_id, revision) on delete cascade
        );
        create table if not exists orders (
          id uuid primary key, name text not null, description text not null,
          order_date date not null, due_date date,
          status text not null check (status in ('Reserved','Started')),
          started_at_utc timestamptz,
          created_at timestamptz not null default now()
        );
        create table if not exists order_lines (
          order_id uuid not null references orders(id) on delete cascade,
          board_id uuid not null, revision integer not null,
          build_quantity bigint not null check (build_quantity > 0),
          primary key (order_id, board_id),
          foreign key (board_id, revision) references board_revisions(board_id, revision) on delete restrict
        );
        create table if not exists reservations (
          order_id uuid not null references orders(id) on delete cascade,
          component_id uuid not null references components(id) on delete restrict,
          quantity bigint not null check (quantity > 0),
          primary key (order_id, component_id)
        );
        create table if not exists production_snapshots (
          order_id uuid primary key references orders(id) on delete restrict,
          schema_version text not null, destination text not null, order_name text not null,
          order_date date not null, due_date date,
          started_at_utc timestamptz not null
        );
        alter table production_snapshots add column if not exists schema_version text not null default '1.0';
        create table if not exists snapshot_boards (
          order_id uuid not null references production_snapshots(order_id) on delete restrict,
          board_id uuid not null, part_number text not null, revision integer not null,
          length_mm numeric(12,3) not null, width_mm numeric(12,3) not null,
          build_quantity bigint not null,
          primary key (order_id, board_id)
        );
        create table if not exists snapshot_components (
          order_id uuid not null, board_id uuid not null,
          component_id uuid not null, part_number text not null,
          quantity_per_board bigint not null, total_required bigint not null,
          primary key (order_id, board_id, component_id),
          foreign key (order_id, board_id) references snapshot_boards(order_id, board_id) on delete restrict
        );
        create index if not exists ix_components_search on components (lower(name), lower(description));
        create index if not exists ix_board_revisions_search on board_revisions (lower(name), lower(description));
        create index if not exists ix_orders_search on orders (lower(name), lower(description));
        """;
}
