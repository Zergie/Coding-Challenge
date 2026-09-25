namespace SmtOrders.Api;

internal static class CatalogSearch
{
    public static bool Match(string name, string description, string? query) => string.IsNullOrWhiteSpace(query) ||
        name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
        description.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

}
