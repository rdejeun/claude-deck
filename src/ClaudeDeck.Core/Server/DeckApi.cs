using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Engine;
using ClaudeDeck.Core.Install;
using ClaudeDeck.Core.Model;
using ClaudeDeck.Core.Platform;
using ClaudeDeck.Core.Rendering;
using ClaudeDeck.Core.Usage;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Server;

/// <summary>Actions propres à l'hôte (installation de l'intégration Claude Code), fournies par l'application Windows.</summary>
public interface IHostActions
{
    bool IntegrationInstalled { get; }
    string Install();
    string Uninstall();
}

/// <summary>
/// Routes HTTP du daemon :
/// <list type="bullet">
/// <item><c>POST /hook</c> — hooks Claude Code (type « http »).</item>
/// <item><c>POST /statusline</c> — JSON de la status line, transmis par <c>claude-deck.exe statusline</c>.</item>
/// <item><c>GET /</c>, <c>/preview</c> — page de réglages et aperçu des touches.</item>
/// <item><c>/api/*</c> — API de la page (jeton requis).</item>
/// </list>
/// Sécurité : écoute sur 127.0.0.1 seulement, contrôle de l'en-tête Host (anti DNS-rebinding), jeton obligatoire
/// pour tout ce qui modifie l'état (une page web tierce ne peut pas poser d'en-tête personnalisé sans CORS).
/// </summary>
public sealed class DeckApi
{
    private readonly ConfigStore _config;
    private readonly SessionTracker _sessions;
    private readonly PlanUsageStore _plan;
    private readonly UsageService? _usage;
    private readonly DeckController _deck;
    private readonly IPlatformServices _platform;
    private readonly IHostActions? _host;
    private readonly TimeProvider _time;
    private readonly int _port;

    public DeckApi(int port, ConfigStore config, SessionTracker sessions, PlanUsageStore plan, UsageService? usage,
        DeckController deck, IPlatformServices platform, IHostActions? host, TimeProvider? time = null)
    {
        _port = port;
        _config = config;
        _sessions = sessions;
        _plan = plan;
        _usage = usage;
        _deck = deck;
        _platform = platform;
        _host = host;
        _time = time ?? TimeProvider.System;
    }

    public async Task<HttpResponse> HandleAsync(HttpRequest req)
    {
        if (!HostAllowed(req.Header("Host"))) return HttpResponse.Text("Forbidden host", 403);

        switch (req.Method, req.Path)
        {
            case ("POST", ClaudeSettingsInstaller.HookPath):
                return Authorized(req) ? Hook(req) : HttpResponse.Empty(401);
            case ("POST", "/statusline"):
                return Authorized(req) ? StatusLine(req) : HttpResponse.Empty(401);
            case ("GET", "/health"):
                return HttpResponse.Text("ok");
            case ("GET", "/"):
            case ("GET", "/settings"):
                return Page("settings.html");
            case ("GET", "/preview"):
                return Page("preview.html");
        }

        if (req.Method == "GET" && req.Path.StartsWith("/key/", StringComparison.Ordinal) && req.Path.EndsWith(".png", StringComparison.Ordinal))
        {
            if (!int.TryParse(req.Path[5..^4], NumberStyles.None, CultureInfo.InvariantCulture, out var k) || k < 0 || k >= DeckController.KeyCount)
                return HttpResponse.Empty(404);
            return new HttpResponse(200, "image/png", KeyRenderer.ToPng(_deck.GetImage(k)));
        }

        if (req.Path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (!Authorized(req) || !OriginAllowed(req.Header("Origin"))) return HttpResponse.Empty(401);
            return await ApiAsync(req).ConfigureAwait(false);
        }

        return HttpResponse.Text("Not Found", 404);
    }

    // ───────────── Claude Code ─────────────

    private HttpResponse Hook(HttpRequest req)
    {
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            if (!req.ConnectionItems.TryGetValue("client", out var client))
            {
                // Le processus à l'autre bout de la connexion TCP est le processus Claude Code lui-même.
                client = SafeResolve(req);
                req.ConnectionItems["client"] = client;
            }
            _sessions.HandleHook(doc.RootElement, client as ClientProcess);
            Log.Debug(DescribeHook(doc.RootElement));
        }
        catch (JsonException ex)
        {
            Log.Warn($"Hook JSON invalide : {ex.Message}");
        }
        // Corps vide + 200 : Claude Code continue normalement (aucune décision n'est prise).
        return HttpResponse.Empty(200);
    }

    /// <summary>Ligne de journal d'un hook : événement, session, outil, agent, tâches d'arrière-plan (diagnostic).</summary>
    internal static string DescribeHook(JsonElement p)
    {
        var sb = new System.Text.StringBuilder("hook ");
        sb.Append(p.Str("hook_event_name")).Append(' ').Append(p.Str("session_id"));
        if (p.Str("tool_name") is { } tool) sb.Append(" tool=").Append(tool);
        if (p.Str("notification_type") is { } nt) sb.Append(" type=").Append(nt);
        if (p.Str("trigger") is { } trigger) sb.Append(" trigger=").Append(trigger);   // PreCompact : manual / auto
        if (p.Str("source") is { } source) sb.Append(" source=").Append(source);       // SessionStart : startup, resume, compact…
        if (p.Str("reason") is { } reason) sb.Append(" reason=").Append(reason);       // SessionEnd
        if (p.Str("agent_id") is { } agent) sb.Append(" agent=").Append(agent);
        if (p.Str("agent_type") is { } at) sb.Append(" agent_type=").Append(at.Length == 0 ? "(interne)" : at);
        if (p.TryGetProperty("background_tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            sb.Append(" background=[");
            sb.AppendJoin(", ", tasks.EnumerateArray().Select(t => $"{t.Str("type")}:{t.Str("status")}"));
            sb.Append(']');
        }
        else if (p.Str("hook_event_name") is "Stop" or "SubagentStop")
        {
            sb.Append(" background=(absent)");
        }
        return sb.ToString();
    }

    private ClientProcess? SafeResolve(HttpRequest req)
    {
        try { return _platform.ResolveClient(req.Remote, _port); }
        catch (Exception ex)
        {
            Log.Warn($"Identification du processus client impossible : {ex.Message}");
            return null;
        }
    }

    private HttpResponse StatusLine(HttpRequest req)
    {
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            var now = _time.GetUtcNow();
            _sessions.HandleStatusLine(doc.RootElement);
            var (five, week) = PlanUsageStore.ParseStatusLine(doc.RootElement, now);
            _plan.Update(five, week);
            return HttpResponse.Text(StatusLineFormatter.Format(doc.RootElement, now));
        }
        catch (JsonException)
        {
            return HttpResponse.Text("ClaudeDeck");
        }
    }

    // ───────────── API de la page ─────────────

    private async Task<HttpResponse> ApiAsync(HttpRequest req)
    {
        var path = req.Path["/api".Length..];
        if (req.Method == "GET" && path == "/state") return HttpResponse.Json(State());
        if (req.Method != "POST") return HttpResponse.Empty(405);

        JsonObject body;
        try
        {
            body = req.Body.Length == 0 ? new JsonObject() : JsonNode.Parse(req.Body) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return HttpResponse.Text("JSON invalide", 400);
        }

        switch (path)
        {
            case var p when p.StartsWith("/press/", StringComparison.Ordinal):
                if (int.TryParse(p[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var key) && key is >= 0 and < DeckController.KeyCount)
                {
                    _deck.Press(key);
                    return HttpResponse.Empty();
                }
                return HttpResponse.Empty(404);

            case "/test-pattern":
                _deck.ShowTestPattern(TimeSpan.FromSeconds(8));
                return HttpResponse.Empty();

            case "/refresh":
                _usage?.RefreshNow();
                return HttpResponse.Empty();

            case "/settings":
                ApplySettings(body);
                return HttpResponse.Json(State());

            case "/admin-key":
            {
                if (_usage is null) return HttpResponse.Text("API désactivée", 400);
                var warning = _usage.SetAdminKey(Str(body, "key"));
                return HttpResponse.Json(new JsonObject { ["warning"] = warning }.ToJsonString());
            }

            case "/admin-key/test":
            {
                if (_usage is null) return HttpResponse.Text("API désactivée", 400);
                var (ok, message) = await _usage.TestAdminKeyAsync(Str(body, "key")).ConfigureAwait(false);
                return HttpResponse.Json(new JsonObject { ["ok"] = ok, ["message"] = message }.ToJsonString());
            }

            case "/balance":
            {
                if (_usage is null) return HttpResponse.Text("API désactivée", 400);
                decimal? amount = null;
                var node = body["balanceUsd"];
                if (node is not null && node.GetValueKind() != JsonValueKind.Null)
                {
                    var raw = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
                    if (!decimal.TryParse(raw.Replace(',', '.').Replace("$", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                        return HttpResponse.Text("Montant invalide", 400);
                    amount = v;
                }
                await _usage.SetBalanceAsync(amount).ConfigureAwait(false);
                return HttpResponse.Json(State());
            }

            case "/install":
                return _host is null ? HttpResponse.Text("Indisponible ici", 400) : HttpResponse.Text(_host.Install());

            case "/uninstall":
                return _host is null ? HttpResponse.Text("Indisponible ici", 400) : HttpResponse.Text(_host.Uninstall());
        }
        return HttpResponse.Empty(404);
    }

    private static string? Str(JsonObject b, string name) =>
        b[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private void ApplySettings(JsonObject b)
    {
        int? I(string k) => b[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : b[k] is JsonValue d && d.TryGetValue<double>(out var x) ? (int)x : null;
        bool? B(string k) => b[k] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : null;
        decimal? D(string k) => b[k] is JsonValue v && v.TryGetValue<decimal>(out var x) ? x : null;

        _config.Update(c =>
        {
            if (I("brightness") is { } br) c.Display.Brightness = Math.Clamp(br, 0, 100);
            if (I("dimBrightness") is { } db) c.Display.DimBrightness = Math.Clamp(db, 0, 100);
            if (I("dimAfterMinutes") is { } dm) c.Display.DimAfterMinutes = Math.Clamp(dm, 0, 24 * 60);
            if (B("offWhenLocked") is { } ol) c.Display.OffWhenLocked = ol;
            if (I("doneFadeMinutes") is { } df) c.Display.DoneFadeMinutes = Math.Clamp(df, 0, 24 * 60);
            if (B("oauthFallback") is { } of) c.PlanUsage.OAuthFallback = of;
            if (Str(b, "bottomRightKey") is BottomRightKeys.Logo or BottomRightKeys.Api) c.Display.BottomRightKey = Str(b, "bottomRightKey")!;
            if (Str(b, "workingAnimation") is WorkingAnimations.Asterisk or WorkingAnimations.Ring or WorkingAnimations.Gif)
                c.Display.WorkingAnimation = Str(b, "workingAnimation")!;
            if (b.ContainsKey("workingAnimationPath"))
                c.Display.WorkingAnimationPath = string.IsNullOrWhiteSpace(Str(b, "workingAnimationPath")) ? null : Str(b, "workingAnimationPath")!.Trim().Trim('"');
            if (b.ContainsKey("logoUrl"))
            {
                var url = Str(b, "logoUrl")?.Trim();
                if (string.IsNullOrEmpty(url)) c.Display.LogoUrl = null;
                else if (BrowserLauncher.IsWebUrl(url)) c.Display.LogoUrl = url;
            }
            if (B("logoUrlNewWindow") is { } nw) c.Display.LogoUrlNewWindow = nw;
            if (b.ContainsKey("logoImagePath"))
                c.Display.LogoImagePath = string.IsNullOrWhiteSpace(Str(b, "logoImagePath")) ? null : Str(b, "logoImagePath")!.Trim().Trim('"');
            if (D("lowBalanceUsd") is { } lb) c.Api.LowBalanceUsd = Math.Max(0, lb);
            if (I("rotation") is { } rot && rot is 0 or 90 or 180 or 270) c.Device.Rotation = rot;
            if (B("flipHorizontal") is { } fh) c.Device.FlipHorizontal = fh;
            if (B("flipVertical") is { } fv) c.Device.FlipVertical = fv;
        });
        _usage?.RefreshNow();
    }

    private string State()
    {
        var cfg = _config.Current;
        var now = _time.GetUtcNow();
        var plan = _plan.Snapshot();
        var api = _usage?.Api;
        var o = new JsonObject
        {
            ["version"] = _deck.Version,
            ["device"] = new JsonObject { ["connected"] = _deck.DeviceConnected, ["description"] = _deck.DeviceStatus },
            ["elgatoRunning"] = SafeBool(_platform.IsElgatoSoftwareRunning),
            ["integrationInstalled"] = _host?.IntegrationInstalled,
            ["sessions"] = new JsonArray(_sessions.Snapshot()
                .OrderBy(s => SlotAllocator.Priority(s.State)).ThenByDescending(s => s.LastEventAt)
                .Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id,
                    ["name"] = s.DisplayName,
                    ["cwd"] = s.Cwd,
                    ["state"] = s.State.ToString(),
                    ["waitReason"] = s.WaitReason.ToString(),
                    ["tool"] = s.CurrentTool,
                    ["model"] = s.Model,
                    ["contextPercent"] = s.ContextUsedPercent,
                    ["lastEventAgoSeconds"] = (int)(now - s.LastEventAt).TotalSeconds,
                    ["pid"] = s.Client?.Pid,
                    ["process"] = s.Client?.Name,
                }).ToArray()),
            ["plan"] = new JsonObject
            {
                ["fiveHour"] = Window(plan.FiveHour),
                ["sevenDay"] = Window(plan.SevenDay),
                ["oauthError"] = cfg.PlanUsage.OAuthFallback ? _usage?.OAuthLastError : null,
            },
            ["api"] = api is null ? null : new JsonObject
            {
                ["configured"] = api.Configured,
                ["spend7dUsd"] = api.Spend7dUsd,
                ["todayUsd"] = api.TodayUsd,
                ["balanceEstimateUsd"] = api.BalanceEstimateUsd,
                ["fetchedAt"] = api.FetchedAt?.ToString("O"),
                ["error"] = api.Error,
                ["errorDetail"] = api.ErrorDetail,
            },
            ["settings"] = new JsonObject
            {
                ["brightness"] = cfg.Display.Brightness,
                ["dimBrightness"] = cfg.Display.DimBrightness,
                ["dimAfterMinutes"] = cfg.Display.DimAfterMinutes,
                ["offWhenLocked"] = cfg.Display.OffWhenLocked,
                ["doneFadeMinutes"] = cfg.Display.DoneFadeMinutes,
                ["oauthFallback"] = cfg.PlanUsage.OAuthFallback,
                ["bottomRightKey"] = cfg.Display.BottomRightKey,
                ["logoImagePath"] = cfg.Display.LogoImagePath,
                ["logoUrl"] = cfg.Display.LogoUrl,
                ["workingAnimation"] = cfg.Display.WorkingAnimation,
                ["workingAnimationPath"] = cfg.Display.WorkingAnimationPath,
                ["logoUrlNewWindow"] = cfg.Display.LogoUrlNewWindow,
                ["lowBalanceUsd"] = cfg.Api.LowBalanceUsd,
                ["balanceUsd"] = cfg.Api.BalanceUsd,
                ["balanceSetAt"] = cfg.Api.BalanceSetAt?.ToString("O"),
                ["rotation"] = cfg.Device.Rotation,
                ["flipHorizontal"] = cfg.Device.FlipHorizontal,
                ["flipVertical"] = cfg.Device.FlipVertical,
                ["port"] = cfg.Port,
            },
        };
        return o.ToJsonString();

        static JsonObject? Window(RateWindow? w) => w is null ? null : new JsonObject
        {
            ["usedPercent"] = w.UsedPercent,
            ["resetsAt"] = w.ResetsAt?.ToString("O"),
            ["observedAt"] = w.ObservedAt.ToString("O"),
            ["source"] = w.Source,
        };
    }

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    // ───────────── Sécurité ─────────────

    private bool Authorized(HttpRequest req)
    {
        var token = req.Header(ClaudeSettingsInstaller.TokenHeader);
        return token is not null && CryptographicEquals(token, _config.Current.Token);
    }

    private bool HostAllowed(string? host) =>
        host is not null && (host == $"127.0.0.1:{_port}" || host == $"localhost:{_port}");

    private bool OriginAllowed(string? origin) =>
        origin is null || origin == $"http://127.0.0.1:{_port}" || origin == $"http://localhost:{_port}";

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    private HttpResponse Page(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ClaudeDeck.Web.{name}");
        if (s is null) return HttpResponse.Text("Page manquante", 500);
        using var reader = new StreamReader(s);
        // Le jeton est injecté dans la page : seule une page servie par cette origine peut le lire.
        var html = reader.ReadToEnd().Replace("{{TOKEN}}", _config.Current.Token, StringComparison.Ordinal);
        return HttpResponse.Html(html);
    }
}
