using System.Globalization;
using System.Text.Json;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Usage;

/// <summary>Coût d'une journée UTC, en dollars.</summary>
public sealed record DailyCost(DateTimeOffset DayStartUtc, decimal Usd);

/// <summary>Client minimal de l'Admin API Anthropic : <c>GET /v1/organizations/cost_report</c> (clé sk-ant-admin…).</summary>
public sealed class AdminCostClient
{
    private const string BaseUrl = "https://api.anthropic.com/v1/organizations/cost_report";
    private readonly HttpClient _http;

    public AdminCostClient(HttpClient http) => _http = http;

    /// <summary>Retourne les coûts quotidiens depuis <paramref name="fromDayUtc"/> (inclus) jusqu'à maintenant, toutes pages confondues.</summary>
    /// <exception cref="AdminApiException">Réponse d'erreur de l'API (statut, type et message d'erreur).</exception>
    public async Task<IReadOnlyList<DailyCost>> FetchDailyAsync(string adminKey, DateTimeOffset fromDayUtc, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var result = new List<DailyCost>();
        string? page = null;
        var start = fromDayUtc.UtcDateTime.Date;
        for (var guard = 0; guard < 50; guard++)
        {
            // Pas de « ending_at » : il est facultatif et une date future pourrait être refusée.
            var url = $"{BaseUrl}?starting_at={Uri.EscapeDataString(start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}" +
                      "&bucket_width=1d&limit=31";
            if (page is not null) url += $"&page={Uri.EscapeDataString(page)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-api-key", adminKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Headers.UserAgent.ParseAdd("claude-deck/0.1");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw AdminApiException.FromResponse((int)resp.StatusCode, body);

            using var doc = JsonDocument.Parse(body);
            var (items, hasMore, next) = ParsePage(doc.RootElement);
            result.AddRange(items);
            if (!hasMore || string.IsNullOrEmpty(next)) break;
            page = next;
        }
        return result;
    }

    /// <summary>
    /// Nettoie une clé collée : espaces, retours à la ligne, guillemets, caractères invisibles,
    /// préfixes « x-api-key: » ou « Bearer ».
    /// </summary>
    public static string? SanitizeKey(string? raw)
    {
        if (raw is null) return null;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '"' or '\'' or '\u200B' or '\u200C' or '\u200D' or '\uFEFF') continue;
            sb.Append(ch);
        }
        var key = sb.ToString();
        foreach (var prefix in new[] { "x-api-key:", "Bearer" })
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) key = key[prefix.Length..];
        return key.Length == 0 ? null : key;
    }

    /// <summary>Une clé Admin API commence par « sk-ant-admin ». Une clé API classique (sk-ant-api…) est refusée par ces routes.</summary>
    public static bool LooksLikeAdminKey(string key) => key.StartsWith("sk-ant-admin", StringComparison.Ordinal);

    /// <summary>Analyse une page de cost_report. Les montants sont des chaînes décimales en cents.</summary>
    internal static (List<DailyCost> Items, bool HasMore, string? NextPage) ParsePage(JsonElement root)
    {
        var items = new List<DailyCost>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in data.EnumerateArray())
            {
                var startAt = bucket.Instant("starting_at");
                if (startAt is null) continue;
                decimal cents = 0;
                if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in results.EnumerateArray())
                    {
                        var currency = r.Str("currency");
                        if (currency is not null && !currency.Equals("USD", StringComparison.OrdinalIgnoreCase)) continue;
                        if (r.TryGetProperty("amount", out var amount))
                        {
                            var s = amount.ValueKind == JsonValueKind.String ? amount.GetString() : amount.GetRawText();
                            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) cents += c;
                        }
                    }
                }
                items.Add(new DailyCost(startAt.Value.ToUniversalTime(), cents / 100m));
            }
        }
        var hasMore = root.Bool("has_more") ?? false;
        var next = root.Str("next_page");
        return (items, hasMore, next);
    }

}

/// <summary>Erreur renvoyée par l'Admin API, avec le statut HTTP et le message d'erreur Anthropic.</summary>
public sealed class AdminApiException : Exception
{
    public AdminApiException(int status, string? errorType, string? apiMessage)
        : base($"HTTP {status}{(errorType is null ? "" : " " + errorType)}{(apiMessage is null ? "" : " : " + apiMessage)}")
    {
        Status = status;
        ErrorType = errorType;
        ApiMessage = apiMessage;
    }

    public int Status { get; }
    public string? ErrorType { get; }
    public string? ApiMessage { get; }

    /// <summary>Analyse un corps d'erreur {"type":"error","error":{"type":…,"message":…}}.</summary>
    public static AdminApiException FromResponse(int status, string body)
    {
        string? type = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var err = doc.RootElement.Obj("error");
            type = err?.Str("type");
            message = err?.Str("message");
        }
        catch (JsonException)
        {
            message = body.Length > 200 ? body[..200] + "…" : body;
        }
        return new AdminApiException(status, type, message);
    }

    /// <summary>Libellé court pour la touche (≈ 14 caractères).</summary>
    public string ShortLabel => Status switch
    {
        401 => "clé refusée",
        403 => "accès refusé",
        400 or 404 or 422 => "requête refusée",
        429 => "trop d'appels",
        >= 500 => "API indisponible",
        _ => $"erreur {Status}",
    };
}

/// <summary>État affiché sur la touche « API ».</summary>
/// <param name="Error">Libellé court (touche).</param>
/// <param name="ErrorDetail">Explication complète (page de réglages).</param>
public sealed record ApiSpendSnapshot(
    bool Configured,
    decimal? Spend7dUsd,
    decimal? TodayUsd,
    decimal? BalanceEstimateUsd,
    DateTimeOffset? FetchedAt,
    string? Error,
    string? ErrorDetail = null);

public static class BalanceEstimator
{
    /// <summary>
    /// Solde estimé = solde saisi − (dépense depuis le début de la journée UTC de saisie − dépense déjà comptée à la saisie).
    /// Le rapport de coûts n'a qu'une granularité journalière, d'où la « ligne de base » mémorisée au moment de la saisie.
    /// </summary>
    public static decimal? Estimate(decimal? balanceUsd, DateTimeOffset? setAt, decimal baselineUsd, IReadOnlyList<DailyCost> daily)
    {
        if (balanceUsd is null || setAt is null) return null;
        var setDay = setAt.Value.UtcDateTime.Date;
        var spentSinceDayStart = daily.Where(d => d.DayStartUtc.UtcDateTime.Date >= setDay).Sum(d => d.Usd);
        return balanceUsd.Value - Math.Max(0, spentSinceDayStart - baselineUsd);
    }
}
