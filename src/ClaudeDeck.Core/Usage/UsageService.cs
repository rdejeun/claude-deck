using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Platform;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Usage;

/// <summary>
/// Interroge périodiquement les sources distantes : Admin API (dépense, solde estimé) et, si activé, l'endpoint OAuth
/// des limites du forfait.
/// </summary>
public sealed class UsageService : IDisposable
{
    public const string AdminKeySecret = "admin-api-key";

    private readonly ConfigStore _config;
    private readonly ISecretStore _secrets;
    private readonly PlanUsageStore _plan;
    private readonly AdminCostClient _admin;
    private readonly OAuthUsageClient _oauth;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshSignal = new(0, int.MaxValue);
    private readonly object _gate = new();

    private ApiSpendSnapshot _api = new(false, null, null, null, null, null);
    private IReadOnlyList<DailyCost> _daily = [];
    private DateTimeOffset _nextApi = DateTimeOffset.MinValue;
    private DateTimeOffset _nextOAuth = DateTimeOffset.MinValue;
    private Task? _loop;

    public UsageService(ConfigStore config, ISecretStore secrets, PlanUsageStore plan, HttpClient http, TimeProvider? time = null)
    {
        _config = config;
        _secrets = secrets;
        _plan = plan;
        _time = time ?? TimeProvider.System;
        _admin = new AdminCostClient(http);
        _oauth = new OAuthUsageClient(http);
        _api = _api with { Configured = HasAdminKey };
    }

    public event Action? Changed;

    public bool HasAdminKey => !string.IsNullOrEmpty(_secrets.Get(AdminKeySecret));

    public ApiSpendSnapshot Api
    {
        get { lock (_gate) return _api; }
    }

    public string? OAuthLastError => _oauth.LastError;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>Force un rafraîchissement immédiat de toutes les sources.</summary>
    public void RefreshNow()
    {
        _nextApi = DateTimeOffset.MinValue;
        _nextOAuth = DateTimeOffset.MinValue;
        _refreshSignal.Release();
    }

    /// <summary>Enregistre la clé (nettoyée) puis lance un rafraîchissement. Retourne un avertissement éventuel.</summary>
    public string? SetAdminKey(string? key)
    {
        var clean = AdminCostClient.SanitizeKey(key);
        _secrets.Set(AdminKeySecret, clean);
        lock (_gate) _api = new ApiSpendSnapshot(HasAdminKey, null, null, null, null, null);
        Changed?.Invoke();
        RefreshNow();
        return clean is not null && !AdminCostClient.LooksLikeAdminKey(clean) ? NotAdminKeyWarning : null;
    }

    private const string NotAdminKeyWarning =
        "Ce n'est pas une clé Admin (sk-ant-admin…). Elle ne fonctionnera que s'il s'agit d'une clé personnelle (« Linked account » = vous) " +
        "NON limitée à un espace de travail, dans un compte d'organisation, avec le rôle admin ou propriétaire.";

    internal const string AccountHint =
        "L'Admin API n'existe pas pour les comptes individuels : créez d'abord l'organisation dans Console → Settings → Organization " +
        "(platform.claude.com/settings/organization), puis une clé dans Settings → Admin keys (rôle admin ou propriétaire requis).";

    /// <summary>Teste la clé enregistrée (ou celle fournie) et renvoie un diagnostic lisible.</summary>
    public async Task<(bool Ok, string Message)> TestAdminKeyAsync(string? candidate, CancellationToken ct = default)
    {
        var key = AdminCostClient.SanitizeKey(candidate) ?? AdminCostClient.SanitizeKey(_secrets.Get(AdminKeySecret));
        if (string.IsNullOrEmpty(key)) return (false, "Aucune clé enregistrée.");
        var now = _time.GetUtcNow();
        try
        {
            var days = await _admin.FetchDailyAsync(key, now.AddDays(-1), now, ct).ConfigureAwait(false);
            return (true, $"Clé acceptée ({days.Count} jour(s) de coûts reçus).");
        }
        catch (AdminApiException ex)
        {
            return (false, Explain(ex, key));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"api.anthropic.com injoignable : {ex.Message}");
        }
    }

    /// <summary>Message détaillé pour la page de réglages.</summary>
    internal static string Explain(AdminApiException ex, string key)
    {
        var api = ex.ApiMessage is null ? "" : $" — réponse de l'API : « {ex.ApiMessage} »";
        var hint = ex.Status switch
        {
            401 when !AdminCostClient.LooksLikeAdminKey(key) => " " + NotAdminKeyWarning,
            401 => " La clé Admin est inconnue, expirée ou révoquée : vérifiez-la dans Console → Settings → Admin keys, ou créez-en une nouvelle.",
            403 => " La clé n'a pas les droits nécessaires (clé limitée à un espace de travail, rôle insuffisant ou compte individuel). " + AccountHint,
            429 => " Trop d'appels : nouvel essai au prochain cycle.",
            _ => "",
        };
        return $"Admin API : HTTP {ex.Status}{(ex.ErrorType is null ? "" : $" ({ex.ErrorType})")}{api}.{hint}";
    }

    /// <summary>Enregistre le solde affiché dans la Console. La dépense du jour déjà connue sert de ligne de base.</summary>
    public async Task SetBalanceAsync(decimal? balanceUsd, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        decimal baseline = 0;
        if (balanceUsd is not null && AdminCostClient.SanitizeKey(_secrets.Get(AdminKeySecret)) is { Length: > 0 } key)
        {
            try
            {
                var today = await _admin.FetchDailyAsync(key, now, now, ct).ConfigureAwait(false);
                baseline = today.Where(d => d.DayStartUtc.UtcDateTime.Date == now.UtcDateTime.Date).Sum(d => d.Usd);
            }
            catch (Exception ex)
            {
                Log.Warn($"Ligne de base du solde indisponible ({ex.Message}) : 0 $ utilisé.");
            }
        }

        _config.Update(c =>
        {
            c.Api.BalanceUsd = balanceUsd;
            c.Api.BalanceSetAt = balanceUsd is null ? null : now;
            c.Api.BalanceBaselineUsd = baseline;
        });
        RefreshNow();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = _time.GetUtcNow();
                var cfg = _config.Current;

                if (cfg.Api.Enabled && now >= _nextApi)
                {
                    _nextApi = now + TimeSpan.FromMinutes(Math.Max(1, cfg.Api.PollMinutes));
                    await RefreshApiAsync(cfg, now, ct).ConfigureAwait(false);
                }

                if (cfg.PlanUsage.OAuthFallback && now >= _nextOAuth)
                {
                    _nextOAuth = now + TimeSpan.FromMinutes(Math.Max(1, cfg.PlanUsage.OAuthPollMinutes));
                    // Inutile d'interroger l'endpoint si une session CLI vient de fournir des valeurs fraîches.
                    var last = _plan.LastObservedAt;
                    if (last is null || now - last > TimeSpan.FromMinutes(2))
                    {
                        var r = await _oauth.FetchAsync(now, ct).ConfigureAwait(false);
                        if (r is { } v) _plan.Update(v.FiveHour, v.SevenDay);
                        else Log.Debug($"OAuth usage : {_oauth.LastError}");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Boucle d'usage", ex);
            }

            try
            {
                await _refreshSignal.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshApiAsync(DeckConfig cfg, DateTimeOffset now, CancellationToken ct)
    {
        // Nettoyée aussi à la lecture : une clé enregistrée avant ce correctif peut contenir des caractères parasites.
        var key = AdminCostClient.SanitizeKey(_secrets.Get(AdminKeySecret));
        if (string.IsNullOrEmpty(key))
        {
            lock (_gate) _api = new ApiSpendSnapshot(false, null, null, null, null, null);
            Changed?.Invoke();
            return;
        }

        // Fenêtre : 7 derniers jours (aujourd'hui inclus), étendue jusqu'au jour de saisie du solde si besoin.
        var from = now.UtcDateTime.Date.AddDays(-6);
        if (cfg.Api.BalanceSetAt is { } setAt && setAt.UtcDateTime.Date < from) from = setAt.UtcDateTime.Date;

        try
        {
            var daily = await _admin.FetchDailyAsync(key, new DateTimeOffset(from, TimeSpan.Zero), now, ct).ConfigureAwait(false);
            var weekStart = now.UtcDateTime.Date.AddDays(-6);
            var spend7 = daily.Where(d => d.DayStartUtc.UtcDateTime.Date >= weekStart).Sum(d => d.Usd);
            var today = daily.Where(d => d.DayStartUtc.UtcDateTime.Date == now.UtcDateTime.Date).Sum(d => d.Usd);
            var balance = BalanceEstimator.Estimate(cfg.Api.BalanceUsd, cfg.Api.BalanceSetAt, cfg.Api.BalanceBaselineUsd, daily);
            lock (_gate)
            {
                _daily = daily;
                _api = new ApiSpendSnapshot(true, spend7, today, balance, now, null);
            }
        }
        catch (AdminApiException ex)
        {
            var detail = Explain(ex, key);
            Log.Warn(detail);
            lock (_gate) _api = _api with { Configured = true, Error = ex.ShortLabel, ErrorDetail = detail };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Admin API : {ex.Message}");
            var label = ex is HttpRequestException or TaskCanceledException ? "hors ligne" : "erreur";
            lock (_gate) _api = _api with { Configured = true, Error = label, ErrorDetail = $"Admin API : {ex.Message}" };
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* arrêt */ }
        _cts.Dispose();
    }
}
