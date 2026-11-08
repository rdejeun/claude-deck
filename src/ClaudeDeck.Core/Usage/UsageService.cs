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

    public void SetAdminKey(string? key)
    {
        _secrets.Set(AdminKeySecret, string.IsNullOrWhiteSpace(key) ? null : key.Trim());
        lock (_gate) _api = new ApiSpendSnapshot(HasAdminKey, null, null, null, null, null);
        Changed?.Invoke();
        RefreshNow();
    }

    /// <summary>Enregistre le solde affiché dans la Console. La dépense du jour déjà connue sert de ligne de base.</summary>
    public async Task SetBalanceAsync(decimal? balanceUsd, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        decimal baseline = 0;
        if (balanceUsd is not null && _secrets.Get(AdminKeySecret) is { Length: > 0 } key)
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
        var key = _secrets.Get(AdminKeySecret);
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Admin API : {ex.Message}");
            lock (_gate) _api = _api with { Configured = true, Error = ShortError(ex) };
        }
        Changed?.Invoke();
    }

    private static string ShortError(Exception ex) => ex.Message.Contains("401") ? "clé refusée"
        : ex.Message.Contains("403") ? "accès refusé"
        : ex is HttpRequestException or TaskCanceledException ? "hors ligne"
        : "erreur";

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* arrêt */ }
        _cts.Dispose();
    }
}
