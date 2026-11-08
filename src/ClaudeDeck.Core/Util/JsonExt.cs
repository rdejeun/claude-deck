using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeDeck.Core.Util;

internal static class JsonExt
{
    public static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;

    /// <summary>Lit un nombre, qu'il soit encodé en nombre JSON ou en chaîne.</summary>
    public static double? Num(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    /// <summary>Lit un instant exprimé en secondes epoch (nombre ou chaîne) ou en ISO 8601.</summary>
    public static DateTimeOffset? Instant(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return FromEpoch(v.GetDouble());
            case JsonValueKind.String:
                var s = v.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return FromEpoch(d);
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) return dto;
                return null;
            default:
                return null;
        }
    }

    private static DateTimeOffset FromEpoch(double value)
    {
        // Tolère les millisecondes (valeurs > année 5000 en secondes).
        if (value > 100_000_000_000) value /= 1000.0;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(value * 1000));
    }

    public static JsonObject? ParseObject(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: Lenient);
        return node as JsonObject;
    }
}
