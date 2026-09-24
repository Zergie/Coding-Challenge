using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmtOrders.Api;

internal static class PartialUpdate
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static T Apply<T>(JsonElement update, T current)
    {
        if (update.ValueKind != JsonValueKind.Object)
        {
            throw new DomainException(400, "invalid_input", "Update body must be a JSON object.");
        }

        var merged = JsonSerializer.SerializeToNode(current, JsonOptions)!.AsObject();
        foreach (var property in update.EnumerateObject())
        {
            var name = merged.Select(x => x.Key).FirstOrDefault(x =>
                string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainException(400, "invalid_input", $"Unknown update field: {property.Name}.");
            merged[name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return merged.Deserialize<T>(JsonOptions)
            ?? throw new DomainException(400, "invalid_input", "Update body is invalid.");
    }
}
