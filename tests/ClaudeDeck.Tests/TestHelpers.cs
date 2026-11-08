using System.Text.Json;

namespace ClaudeDeck.Tests;

/// <summary>Horloge manipulable pour les tests.</summary>
internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan d) => Now += d;
}

internal static class J
{
    public static JsonElement E(object o) => JsonSerializer.SerializeToElement(o);

    public static JsonElement Hook(string session, string evt, object? extra = null)
    {
        var dict = new Dictionary<string, object?> { ["session_id"] = session, ["hook_event_name"] = evt, ["cwd"] = "/dev/" + session };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) dict[p.Name] = p.GetValue(extra);
        return JsonSerializer.SerializeToElement(dict);
    }
}
