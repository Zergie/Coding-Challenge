using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace SmtOrders.Api;

// All demo data shares a partition. Every write also replaces this version row with
// its ETag, so a batch planned from stale reads cannot commit after another writer.
public sealed class Database(TableClient table)
{
    public const string Partition = "demo";
    private const string VersionKey = "M:version";

    public async Task Initialize()
    {
        await table.CreateIfNotExistsAsync();
        try { await table.AddEntityAsync(new TableEntity(Partition, VersionKey) { ["Value"] = 0L }); }
        catch (RequestFailedException e) when (e.Status == 409) { }
    }

    public async Task<TableEntity?> Get(string key)
    {
        var response = await table.GetEntityIfExistsAsync<TableEntity>(Partition, key);
        return response.HasValue ? response.Value : null;
    }

    public async Task<List<TableEntity>> List(string prefix)
    {
        var rows = new List<TableEntity>();
        var upper = prefix[..^1] + (char)(prefix[^1] + 1);
        await foreach (var entity in table.QueryAsync<TableEntity>(
            x => x.PartitionKey == Partition && x.RowKey.CompareTo(prefix) >= 0 && x.RowKey.CompareTo(upper) < 0))
            rows.Add(entity);
        return rows;
    }

    public async Task<T> ReadStable<T>(Func<Task<T>> read)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var before = await Get(VersionKey);
            T result;
            try { result = await read(); }
            catch (StaleReadException) { continue; }
            if (before?.ETag == (await Get(VersionKey))?.ETag) return result;
        }
        throw new DomainException(409, "read_conflict", "The data changed concurrently; please retry.");
    }

    public async Task<T> Write<T>(Func<ChangeSet, Task<T>> plan)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var version = await Get(VersionKey) ?? throw new InvalidOperationException("Table version row is missing.");
            var changes = new ChangeSet();
            T result;
            try { result = await plan(changes); }
            catch (StaleReadException) { continue; }
            if (changes.Actions.Count == 0) return result;
            var next = new TableEntity(Partition, VersionKey) { ["Value"] = (long)version["Value"] + 1 };
            changes.Actions.Add(new(TableTransactionActionType.UpdateReplace, next, version.ETag));
            if (changes.Actions.Count > 100)
                throw new DomainException(400, "batch_limit", "This change exceeds the 100-entity Table Storage transaction limit.");
            var approximateBytes = changes.Actions.Sum(action => action.Entity is TableEntity row
                ? row.Sum(property => property.Key.Length * 2L + (property.Value is string value
                    ? System.Text.Encoding.UTF8.GetByteCount(value) : 32)) + 256
                : 256);
            if (approximateBytes > 3_500_000)
                throw new DomainException(400, "batch_limit", "This change exceeds the Table Storage transaction size limit.");
            try
            {
                await table.SubmitTransactionAsync(changes.Actions);
                return result;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412 or 404)
            {
                // A concurrent writer changed the version. Re-read and re-plan.
            }
        }
        throw new DomainException(409, "write_conflict", "The data changed concurrently; please retry.");
    }

    public sealed class ChangeSet
    {
        internal List<TableTransactionAction> Actions { get; } = [];
        public void Add(TableEntity row) => Actions.Add(new(TableTransactionActionType.Add, row));
        public void Replace(TableEntity old, TableEntity row) => Actions.Add(new(TableTransactionActionType.UpdateReplace, row, old.ETag));
        public void Delete(TableEntity old) => Actions.Add(new(TableTransactionActionType.Delete, old, old.ETag));
    }

    public static TableEntity Row<T>(string key, T data)
    {
        var json = JsonSerializer.Serialize(data);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 60_000)
            throw new DomainException(400, "entity_limit", "This record exceeds the Table Storage entity size limit.");
        return new(Partition, key) { ["Json"] = json };
    }
    public static T Data<T>(TableEntity row) => JsonSerializer.Deserialize<T>((string)row["Json"])!;
    public static string Id(Guid id) => id.ToString("N");
    public static string PartKey(string type, string part) => type + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(part.Trim().ToUpperInvariant())));

    public static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException(400, "invalid_input", $"{field} is required.");
        if (value.Length > 2000) throw new DomainException(400, "invalid_input", $"{field} is too long.");
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
    public static DomainException Duplicate() => new(409, "duplicate", "A record with this unique value already exists.");
}

internal sealed class StaleReadException : Exception;
