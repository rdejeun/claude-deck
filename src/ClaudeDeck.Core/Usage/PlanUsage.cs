using System.Text.Json;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Usage;

/// <summary>Une fenêtre de limite d'usage du forfait Pro/Max (5 h glissantes ou 7 jours).</summary>
public sealed record RateWindow(double UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset ObservedAt, string Source)
{
    /// <summary>La fenêtre est réinitialisée depuis l'observation : la valeur n'est plus valable.</summary>
    public bool HasReset(DateTimeOffset now) => ResetsAt is { } r && now >= r;
}

/// <summary>
/// Crédits d'utilisation (« extra usage ») d'un forfait Pro/Max : utilisés quand une limite du forfait est atteinte.
/// Montants dans la devise du compte. Seule source : l'endpoint OAuth (non documenté) de la commande /usage.
/// </summary>
public sealed record UsageCredits(bool Enabled, decimal? MonthlyLimit, decimal? Used, string Currency, DateTimeOffset ObservedAt)
{
    /// <summary>Solde de crédits prépayés (endpoint prepaid/credits), ou null si inconnu.</summary>
    public decimal? Balance { get; init; }

    /// <summary>Recharge automatique des crédits activée, ou null si inconnu.</summary>
    public bool? AutoReload { get; init; }

    /// <summary>Reste disponible jusqu'au plafond mensuel, ou null sans plafond.</summary>
    public decimal? Remaining => MonthlyLimit is { } l && Used is { } u ? Math.Max(0, l - u) : null;

    /// <summary>Part du plafond consommée (0-100), ou null sans plafond.</summary>
    public double? UsedPercent => MonthlyLimit is > 0 && Used is { } u ? (double)(u / MonthlyLimit.Value * 100m) : null;
}

public sealed record PlanUsageSnapshot(RateWindow? FiveHour, RateWindow? SevenDay, UsageCredits? Credits = null);

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
    private UsageCredits? _credits;

    public PlanUsageStore(string? persistPath = null)
    {
        _persistPath = persistPath;
        Load();
    }

    public event Action? Changed;

    public PlanUsageSnapshot Snapshot()
    {
        lock (_gate) return new PlanUsageSnapshot(_fiveHour, _sevenDay, _credits);
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
            changed = Merge(ref _fiveHour, fiveHour, ref _staleFive) | Merge(ref _sevenDay, sevenDay, ref _staleWeek);
        }
        if (!changed) return;
        Persist();
        Changed?.Invoke();
    }

    /// <summary>
    /// Fenêtre (repérée par son resets_at) dont la source OAuth a constaté la fin (réinitialisation, y compris gratuite
    /// depuis claude.ai). La status line continue d'envoyer ses anciennes valeurs jusqu'à la réponse suivante de Claude :
    /// on les ignore pour ne pas revenir à 100 %.
    /// </summary>
    private DateTimeOffset? _staleFive, _staleWeek;

    private static bool Merge(ref RateWindow? current, RateWindow? incoming, ref DateTimeOffset? staleWindow)
    {
        if (incoming is null) return false;
        if (current is not null && incoming.ObservedAt < current.ObservedAt) return false;
        if (incoming.Source == "statusline" && staleWindow is { } stale && incoming.ResetsAt == stale) return false;
        if (incoming.Source == "oauth" && current is { Source: "statusline" } cur && cur.ResetsAt is { } curReset &&
            (incoming.ResetsAt != curReset || incoming.UsedPercent < cur.UsedPercent - 1))
            staleWindow = curReset;
        // Même à valeur égale, l'observation est plus récente : l'indicateur d'ancienneté change.
        current = incoming;
        return true;
    }

    public void UpdateCredits(UsageCredits? credits)
    {
        if (credits is null) return;
        lock (_gate) _credits = credits;
        Persist();
        Changed?.Invoke();
    }

    /// <summary>
    /// Réponse de <c>GET /api/oauth/organizations/{org}/prepaid/credits</c> : <c>amount</c> (solde, unités mineures),
    /// <c>currency</c>, <c>auto_reload_settings.enabled</c>.
    /// </summary>
    public static (decimal? Balance, string? Currency, bool? AutoReload) ParsePrepaid(JsonElement payload)
    {
        decimal? balance = payload.Num("amount") is { } a ? (decimal)a / 100m : null;
        var reload = payload.Obj("auto_reload_settings")?.Bool("enabled");
        return (balance, payload.Str("currency")?.Trim().ToUpperInvariant(), reload);
    }

    /// <summary>
    /// Extrait le bloc <c>extra_usage</c> de la réponse OAuth : <c>is_enabled</c>, <c>monthly_limit</c> et
    /// <c>used_credits</c> en unités mineures (centimes, selon <c>decimal_places</c>, 2 par défaut), <c>currency</c>.
    /// </summary>
    public static UsageCredits? ParseOAuthCredits(JsonElement payload, DateTimeOffset now)
    {
        var e = payload.Obj("extra_usage");
        if (e is null) return null;
        var enabled = e.Value.Bool("is_enabled") ?? false;
        var places = (int)Math.Clamp(e.Value.Num("decimal_places") ?? 2, 0, 6);
        var divisor = (decimal)Math.Pow(10, places);
        decimal? Money(string name) => e.Value.Num(name) is { } v ? (decimal)v / divisor : null;
        var currency = e.Value.Str("currency");
        return new UsageCredits(enabled, Money("monthly_limit"), Money("used_credits"),
            string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant(), now);
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

    private sealed record Persisted(RateWindow? FiveHour, RateWindow? SevenDay, UsageCredits? Credits = null);

    private void Persist()
    {
        if (_persistPath is null) return;
        try
        {
            Persisted p;
            lock (_gate) p = new Persisted(_fiveHour, _sevenDay, _credits);
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
            _credits = p?.Credits;
        }
        catch (Exception ex)
        {
            Log.Warn($"État des jauges illisible, ignoré : {ex.Message}");
        }
    }
}
