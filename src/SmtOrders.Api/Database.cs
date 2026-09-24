using Npgsql;

namespace SmtOrders.Api;

public sealed class Database(NpgsqlDataSource source)
{
    public async Task<T> Read<T>(Func<NpgsqlConnection, Task<T>> action)
    {
        await using var connection = await source.OpenConnectionAsync();
        return await action(connection);
    }

    public async Task<T> Write<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // All inventory, recipe and order writes take the same database lock. This
            // serializes competing reservations across API instances, not just threads.
            await using (var command = Command(connection, transaction, "select pg_advisory_xact_lock(7493401)"))
                await command.ExecuteNonQueryAsync();
            var result = await action(connection, transaction);
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string sql, params (string Name, object? Value)[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException(400, "invalid_input", $"{field} is required.");
    }

    public static void Positive(long value, string field)
    {
        if (value <= 0) throw new DomainException(400, "invalid_input", $"{field} must be positive.");
    }

    public static void Positive(decimal value, string field)
    {
        if (value <= 0 || value > 999999999m || decimal.Round(value, 3) != value)
            throw new DomainException(400, "invalid_input", $"{field} must be positive with at most three decimal places.");
    }

    public static DomainException Missing(string name) => new(404, "not_found", $"{name} was not found.");
    public static DomainException Referenced(string name) => new(409, "referenced", $"{name} is referenced and cannot be removed.");
}
