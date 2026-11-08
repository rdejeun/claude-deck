using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Server;

/// <summary>Texte par défaut de la status line Claude Code quand l'utilisateur n'en avait pas.</summary>
public static class StatusLineFormatter
{
    public static string Format(JsonElement p, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var parts = new List<string>();

        var model = p.Obj("model")?.Str("display_name");
        if (!string.IsNullOrEmpty(model)) parts.Add(model);

        if (p.Obj("context_window")?.Num("used_percentage") is { } ctx)
            parts.Add($"ctx {ctx.ToString("0", CultureInfo.InvariantCulture)}%");

        var rl = p.Obj("rate_limits");
        if (rl?.Obj("five_hour") is { } five && five.Num("used_percentage") is { } fivePct)
        {
            var s = $"5h {fivePct.ToString("0", CultureInfo.InvariantCulture)}%";
            if (five.Instant("resets_at") is { } r) s += " →" + TimeZoneInfo.ConvertTime(r, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
            parts.Add(s);
        }
        if (rl?.Obj("seven_day") is { } week && week.Num("used_percentage") is { } weekPct)
            parts.Add($"7j {weekPct.ToString("0", CultureInfo.InvariantCulture)}%");

        if (parts.Count == 0) return "ClaudeDeck";
        var sb = new StringBuilder();
        sb.AppendJoin(" · ", parts);
        return sb.ToString();
    }
}
