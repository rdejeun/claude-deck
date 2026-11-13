using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Usage;

/// <summary>
/// Source OPTIONNELLE des jauges du forfait : l'endpoint OAuth que Claude Code interroge pour sa commande /usage.
/// Cet endpoint n'est pas documenté publiquement et peut changer sans préavis : en cas d'échec, on se contente
/// de la status line. Le jeton est lu en lecture seule dans ~/.claude/.credentials.json et n'est jamais rafraîchi
/// par ClaudeDeck (Claude Code s'en charge lorsqu'il tourne).
/// </summary>
public sealed class OAuthUsageClient
{
    private static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    private readonly HttpClient _http;
    private readonly string _credentialsPath;

    public OAuthUsageClient(HttpClient http, string? claudeConfigDir = null)
    {
        _http = http;
        _credentialsPath = Path.Combine(claudeConfigDir ?? Install.ClaudePaths.ConfigDir(), ".credentials.json");
    }

    public string? LastError { get; private set; }

    /// <summary>Code HTTP de la dernière réponse en erreur (429 = endpoint limité : espacer les appels).</summary>
    public int? LastStatus { get; private set; }

    public sealed record Result(RateWindow? FiveHour, RateWindow? SevenDay, UsageCredits? Credits);

    public async Task<Result?> FetchAsync(DateTimeOffset now, CancellationToken ct)
    {
        var token = ReadAccessToken(now);
        if (token is null)
        {
            LastError = "jeton OAuth absent ou expiré";
            return null;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("claude-deck/0.1");

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastStatus = (int)resp.StatusCode;
                LastError = $"HTTP {(int)resp.StatusCode}";
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            LastError = null;
            LastStatus = null;
            var (five, week) = PlanUsageStore.ParseOAuth(doc.RootElement, now);
            var credits = PlanUsageStore.ParseOAuthCredits(doc.RootElement, now);
            if (doc.RootElement.Obj("extra_usage") is { } extra) Log.Debug($"OAuth extra_usage : {extra.GetRawText()}");
            return new Result(five, week, credits);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private string? ReadAccessToken(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(_credentialsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_credentialsPath));
            var oauth = doc.RootElement.Obj("claudeAiOauth");
            var token = oauth?.Str("accessToken");
            var expires = oauth?.Instant("expiresAt");
            if (string.IsNullOrEmpty(token)) return null;
            if (expires is { } e && e <= now) return null;
            return token;
        }
        catch (Exception ex)
        {
            Log.Warn($"Lecture des identifiants Claude impossible : {ex.Message}");
            return null;
        }
    }
}
