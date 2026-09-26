using System.Text.Json;
using System.Globalization;
using System.Reflection;
using Azure;
using Azure.Data.Tables;

namespace SmtOrders.Api;

[AttributeUsage(AttributeTargets.Field)]
internal sealed class RowPrefixAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}

internal enum PartNumberIndex
{
    [RowPrefix("CP:")] Component,
    [RowPrefix("BP:")] Board
}

// All demo data shares a partition. Every write also replaces this version row with
// its ETag, so a batch planned from stale reads cannot commit after another writer.
public sealed class Database(TableClient table)
{
    public const string Partition = "demo";
    private const string VersionKey = "M:version";
    private static readonly IReadOnlyDictionary<PartNumberIndex, RowPrefixAttribute> IndexPrefixes =
        Enum.GetValues<PartNumberIndex>().ToDictionary(index => index, index =>
            typeof(PartNumberIndex).GetField(index.ToString())!.GetCustomAttribute<RowPrefixAttribute>()
            ?? throw new InvalidOperationException($"PartNumberIndex.{index} has no row prefix."));

    private static RowPrefixAttribute Prefix(PartNumberIndex index) => IndexPrefixes.TryGetValue(index, out var prefix)
        ? prefix : throw new ArgumentOutOfRangeException(nameof(index));

    public async Task Clear()
    {
        try { await table.DeleteAsync(); }
        catch (RequestFailedException e) when (e.Status == 404) { }
        await Initialize();
    }

    public async Task Initialize()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await table.CreateIfNotExistsAsync();
                break;
            }
            catch (RequestFailedException e) when (e.ErrorCode == "TableBeingDeleted" && attempt < 59)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
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
    internal static string ComponentKey(Guid id) => "C:" + Id(id);
    internal static string BoardKey(Guid id) => "B:" + Id(id);
    internal static string BoardRevisionPrefix(Guid id) => $"BR:{Id(id)}:";
    internal static string BoardRevisionKey(Guid id, int revision) => $"{BoardRevisionPrefix(id)}{revision:D8}";
    internal static string OrderKey(Guid id) => "O:" + Id(id);
    internal static string SnapshotHeaderKey(Guid orderId) => "S:" + Id(orderId);
    internal static string SnapshotBoardPrefix(Guid orderId) => $"SB:{Id(orderId)}:";
    internal static string SnapshotBoardKey(Guid orderId, Guid boardId, int revision) =>
        $"{SnapshotBoardPrefix(orderId)}{Id(boardId)}:{revision:D10}";
    internal static string SnapshotComponentKey(Guid orderId, Guid boardId, int revision, Guid componentId) =>
        $"SC:{SnapshotBoardKey(orderId, boardId, revision)[3..]}:{Id(componentId)}";
    internal static string PartKey(PartNumberIndex index, string partNumber)
    {
        var prefix = Prefix(index).Key;
        return prefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(partNumber.Trim().ToUpperInvariant())));
    }

    internal static TableEntity ComponentRow(ComponentRecord record) => Row(ComponentKey(record.Id), record);
    internal static TableEntity BoardRow(BoardRecord record) => Row(BoardKey(record.Id), record);
    internal static TableEntity BoardRevisionRow(BoardRevisionRecord record) =>
        Row(BoardRevisionKey(record.BoardId, record.Revision), record);
    internal static TableEntity OrderRow(OrderRecord record) => Row(OrderKey(record.Id), record);

    internal static ComponentRecord Component(TableEntity row) =>
        Data<ComponentRecord>(row) with { Id = ParseGuid(row.RowKey, "C:") };
    internal static BoardRecord Board(TableEntity row) =>
        Data<BoardRecord>(row) with { Id = ParseGuid(row.RowKey, "B:") };
    internal static BoardRevisionRecord BoardRevision(TableEntity row)
    {
        var (id, revision) = ParseRevision(row.RowKey, "BR:");
        return Data<BoardRevisionRecord>(row) with { BoardId = id, Revision = revision };
    }
    internal static OrderRecord Order(TableEntity row) =>
        Data<OrderRecord>(row) with { Id = ParseGuid(row.RowKey, "O:") };
    internal static (Guid BoardId, int Revision) SnapshotBoard(TableEntity row, Guid orderId)
    {
        var key = row.RowKey;
        var prefix = SnapshotBoardPrefix(orderId);
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException($"Unexpected RowKey: {key}");
        return ParseRevision(key, prefix);
    }
    internal static string SnapshotComponentPrefix(TableEntity boardRow, Guid orderId)
    {
        var prefix = SnapshotBoardPrefix(orderId);
        if (!boardRow.RowKey.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected RowKey: {boardRow.RowKey}");
        return $"SC:{boardRow.RowKey[3..]}:";
    }
    internal static Guid SnapshotComponentId(TableEntity row, TableEntity boardRow, Guid orderId) =>
        ParseGuid(row.RowKey, SnapshotComponentPrefix(boardRow, orderId));

    private static Guid ParseGuid(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException($"Unexpected RowKey: {key}");
        return Guid.ParseExact(key[prefix.Length..], "N");
    }
    private static (Guid Id, int Revision) ParseRevision(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException($"Unexpected RowKey: {key}");
        var suffix = key[prefix.Length..];
        var separator = suffix.IndexOf(':');
        if (separator < 0) throw new InvalidOperationException($"Unexpected RowKey: {key}");
        return (Guid.ParseExact(suffix[..separator], "N"),
            int.Parse(suffix[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture));
    }

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
