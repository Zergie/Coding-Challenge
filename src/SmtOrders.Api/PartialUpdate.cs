using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmtOrders.Api;

internal static class PartialUpdate
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static T Apply<T>(JsonElement update, T current)
    {
        if (update.ValueKind != JsonValueKind.Object)
            throw new DomainException(400, "invalid_input", "Update body must be a JSON object.");

        var merged = JsonSerializer.SerializeToNode(current, JsonOptions)!.AsObject();
        foreach (var property in update.EnumerateObject())
        {
            var name = merged.Select(x => x.Key).FirstOrDefault(x =>
                string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase));
            if (name is null)
                throw new DomainException(400, "invalid_input", $"Unknown update field: {property.Name}.");
            merged[name] = JsonNode.Parse(property.Value.GetRawText());
        }

        try
        {
            return merged.Deserialize<T>(JsonOptions)
                ?? throw new DomainException(400, "invalid_input", "Update body is invalid.");
        }
        catch (JsonException)
        {
            throw new DomainException(400, "invalid_input", "Update body contains an invalid field value.");
        }
    }
}
