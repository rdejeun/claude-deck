using System.Text.Json;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Usage;

/// <summary>Une fenêtre de limite d'usage du forfait Pro/Max (5 h glissantes ou 7 jours).</summary>
public sealed record RateWindow(double UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset ObservedAt, string Source)
{
    /// <summary>La fenêtre est réinitialisée depuis l'observation : la valeur n'est plus valable.</summary>
    public bool HasReset(DateTimeOffset now) => ResetsAt is { } r && now >= r;
}

public sealed record PlanUsageSnapshot(RateWindow? FiveHour, RateWindow? SevenDay);

/// <summary>
/// Garde la dernière valeur connue des limites du forfait, quelle que soit la source
/// (status line d'une session CLI, ou endpoint OAuth optionnel). Persistée pour survivre aux redémarrages.
/// </summary>
public sealed class PlanUsageStore
{
    private readonly object _gate = new();
    private readonly string? _persistPath;
    private RateWindow? _fiveHour;
    private RateWindow? _sevenDay;

    public PlanUsageStore(string? persistPath = null)
    {
        _persistPath = persistPath;
        Load();
    }

    public event Action? Changed;

    public PlanUsageSnapshot Snapshot()
    {
        lock (_gate) return new PlanUsageSnapshot(_fiveHour, _sevenDay);
    }

    /// <summary>Dernière observation, toutes fenêtres confondues.</summary>
    public DateTimeOffset? LastObservedAt
    {
        get
        {
            lock (_gate)
            {
                var a = _fiveHour?.ObservedAt;
                var b = _sevenDay?.ObservedAt;
                return a > b ? a : b ?? a;
            }
        }
    }

    public void Update(RateWindow? fiveHour, RateWindow? sevenDay)
    {
        bool changed;
        lock (_gate)
        {
            changed = Merge(ref _fiveHour, fiveHour) | Merge(ref _sevenDay, sevenDay);
        }
        if (!changed) return;
        Persist();
        Changed?.Invoke();
    }

    private static bool Merge(ref RateWindow? current, RateWindow? incoming)
    {
        if (incoming is null) return false;
        if (current is not null && incoming.ObservedAt < current.ObservedAt) return false;
        // Même à valeur égale, l'observation est plus récente : l'indicateur d'ancienneté change.
        current = incoming;
        return true;
    }

    /// <summary>Extrait <c>rate_limits</c> du JSON envoyé à la status line (présent uniquement pour Pro/Max/Ultra).</summary>
    public static (RateWindow? FiveHour, RateWindow? SevenDay) ParseStatusLine(JsonElement payload, DateTimeOffset now)
    {
        var rl = payload.Obj("rate_limits");
        if (rl is null) return (null, null);
        return (ParseWindow(rl.Value.Obj("five_hour"), now, "statusline", "used_percentage"),
                ParseWindow(rl.Value.Obj("seven_day"), now, "statusline", "used_percentage"));
    }

    /// <summary>Extrait la réponse de l'endpoint OAuth (champs <c>utilization</c> / <c>resets_at</c>).</summary>
    public static (RateWindow? FiveHour, RateWindow? SevenDay) ParseOAuth(JsonElement payload, DateTimeOffset now) =>
        (ParseWindow(payload.Obj("five_hour"), now, "oauth", "utilization", "used_percentage"),
         ParseWindow(payload.Obj("seven_day"), now, "oauth", "utilization", "used_percentage"));

    private static RateWindow? ParseWindow(JsonElement? w, DateTimeOffset now, string source, params string[] percentFields)
    {
        if (w is null) return null;
        double? pct = null;
        foreach (var f in percentFields)
        {
            pct = w.Value.Num(f);
            if (pct is not null) break;
        }
        if (pct is null) return null;
        return new RateWindow(Math.Clamp(pct.Value, 0, 1000), w.Value.Instant("resets_at"), now, source);
    }

    private sealed record Persisted(RateWindow? FiveHour, RateWindow? SevenDay);

    private void Persist()
    {
        if (_persistPath is null) return;
        try
        {
            Persisted p;
            lock (_gate) p = new Persisted(_fiveHour, _sevenDay);
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(p, Config.ConfigJson.Options));
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Impossible d'enregistrer l'état des jauges : {ex.Message}");
        }
    }

    private void Load()
    {
        if (_persistPath is null || !File.Exists(_persistPath)) return;
        try
        {
            var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_persistPath), Config.ConfigJson.Options);
            _fiveHour = p?.FiveHour;
            _sevenDay = p?.SevenDay;
        }
        catch (Exception ex)
        {
            Log.Warn($"État des jauges illisible, ignoré : {ex.Message}");
        }
    }
}
