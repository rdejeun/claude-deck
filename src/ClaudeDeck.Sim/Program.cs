using System.Text.Json;
using ClaudeDeck.Core;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Platform;
using ClaudeDeck.Core.Util;

// Simulateur ClaudeDeck.
//   claude-deck-sim [--port N] [--config DIR] [--demo] [--hardware]
//   --demo      rejoue en boucle un scénario de sessions fictives
//   --hardware  pilote aussi un vrai Stream Deck Mini (Linux : accès hidraw requis)

var argList = args.ToList();
string? Arg(string name) { var i = argList.IndexOf(name); return i >= 0 && i + 1 < argList.Count ? argList[i + 1] : null; }

if (Arg("--gallery") is { } galleryDir)
{
    Gallery.Render(galleryDir);
    return;
}

var configDir = Arg("--config") ?? Path.Combine(Path.GetTempPath(), "claude-deck-sim");
Log.EchoToConsole = true;
Log.Init(Path.Combine(configDir, "logs"));

var store = new ConfigStore(configDir);
if (Arg("--port") is { } port) store.Update(c => c.Port = int.Parse(port));

var platform = new NullPlatform(configDir);
using var host = new DeckHost(store, platform, hostActions: null, useHardware: argList.Contains("--hardware"));
host.Start();

Console.WriteLine();
Console.WriteLine($"  Aperçu   : {host.BaseUrl}/preview");
Console.WriteLine($"  Réglages : {host.BaseUrl}/settings");
Console.WriteLine($"  Hook URL : {host.BaseUrl}/hook   (en-tête X-ClaudeDeck-Token: {store.Current.Token})");
Console.WriteLine("  Ctrl+C pour quitter.");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (argList.Contains("--demo")) _ = Task.Run(() => Demo.RunAsync(host, cts.Token));

try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }

static class Demo
{
    private static void Hook(DeckHost host, object payload) =>
        host.Sessions.HandleHook(JsonSerializer.SerializeToElement(payload));

    public static async Task RunAsync(DeckHost host, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Jauges fictives (comme si une status line les avait envoyées).
        var fake = JsonSerializer.SerializeToElement(new
        {
            session_id = "demo",
            rate_limits = new
            {
                five_hour = new { used_percentage = 62.4, resets_at = now.AddHours(2).ToUnixTimeSeconds() },
                seven_day = new { used_percentage = 23.0, resets_at = now.AddDays(3).ToUnixTimeSeconds() },
            },
        });
        var (five, week) = ClaudeDeck.Core.Usage.PlanUsageStore.ParseStatusLine(fake, now);
        host.Plan.Update(five, week);

        var script = new (int DelayMs, object Payload)[]
        {
            (0, new { session_id = "s-api", hook_event_name = "SessionStart", cwd = @"C:\dev\api-gateway", source = "startup" }),
            (300, new { session_id = "s-web", hook_event_name = "SessionStart", cwd = @"C:\dev\webshop", source = "startup" }),
            (300, new { session_id = "s-docs", hook_event_name = "SessionStart", cwd = @"C:\dev\docs", source = "startup" }),
            (300, new { session_id = "s-infra", hook_event_name = "SessionStart", cwd = @"C:\dev\infra", source = "startup" }),
            (800, new { session_id = "s-api", hook_event_name = "UserPromptSubmit", prompt = "corrige le test" }),
            (600, new { session_id = "s-api", hook_event_name = "PreToolUse", tool_name = "Bash" }),
            (900, new { session_id = "s-web", hook_event_name = "UserPromptSubmit", prompt = "ajoute le panier" }),
            (600, new { session_id = "s-web", hook_event_name = "PreToolUse", tool_name = "Edit" }),
            (2500, new { session_id = "s-web", hook_event_name = "PermissionRequest", tool_name = "Bash" }),
            (4000, new { session_id = "s-api", hook_event_name = "PostToolUse", tool_name = "Bash" }),
            (1500, new { session_id = "s-api", hook_event_name = "Stop" }),
            (3000, new { session_id = "s-web", hook_event_name = "PostToolUse", tool_name = "Bash" }),
            (500, new { session_id = "s-docs", hook_event_name = "UserPromptSubmit", prompt = "résume" }),
            (500, new { session_id = "s-docs", hook_event_name = "PreCompact", trigger = "auto" }),
            (3000, new { session_id = "s-docs", hook_event_name = "PostCompact" }),
            (2000, new { session_id = "s-docs", hook_event_name = "Stop" }),
            (2000, new { session_id = "s-web", hook_event_name = "Stop" }),
            (5000, new { session_id = "s-api", hook_event_name = "SessionEnd", reason = "other" }),
            (0, new { session_id = "s-web", hook_event_name = "SessionEnd", reason = "other" }),
            (0, new { session_id = "s-docs", hook_event_name = "SessionEnd", reason = "other" }),
            (0, new { session_id = "s-infra", hook_event_name = "SessionEnd", reason = "other" }),
        };

        while (!ct.IsCancellationRequested)
        {
            foreach (var (delay, payload) in script)
            {
                await Task.Delay(delay, ct);
                Hook(host, payload);
            }
            await Task.Delay(3000, ct);
        }
    }
}

/// <summary>Rend chaque état de touche en PNG (documentation, vérification visuelle).</summary>
static class Gallery
{
    public static void Render(string dir)
    {
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;
        var b = new ClaudeDeck.Core.Rendering.KeyViewBuilder(TimeZoneInfo.Utc)
        {
            WorkingAnimation = ClaudeDeck.Core.Rendering.SpriteAnimation.LoadEmbeddedClaudeSpinner(),
        };
        using var r = new ClaudeDeck.Core.Rendering.KeyRenderer(80);
        ClaudeDeck.Core.Model.SessionSnapshot S(ClaudeDeck.Core.Model.SessionState st, string? tool = null,
            ClaudeDeck.Core.Model.WaitReason wr = ClaudeDeck.Core.Model.WaitReason.None, int agents = 0, double? ctx = null) =>
            new("id", "api-gateway", @"C:\dev\api-gateway", null, "Opus 5.5", st, now.AddSeconds(-95), wr, tool, tool,
                now.AddSeconds(-252), null, agents, ctx, null, now, null);
        var views = new (string Name, ClaudeDeck.Core.Rendering.KeyView View)[]
        {
            ("01-idle", b.Session(S(ClaudeDeck.Core.Model.SessionState.Idle, ctx: 34), now, 0)),
            ("02-working-thinking", b.Session(S(ClaudeDeck.Core.Model.SessionState.Working), now, 250)),
            ("03-working-bash", b.Session(S(ClaudeDeck.Core.Model.SessionState.Working, "Bash"), now, 500)),
            ("04-working-agents", b.Session(S(ClaudeDeck.Core.Model.SessionState.Working, agents: 2), now, 750)),
            ("05-waiting-permission-on", b.Session(S(ClaudeDeck.Core.Model.SessionState.Waiting, "Bash", ClaudeDeck.Core.Model.WaitReason.Permission), now, 0)),
            ("06-waiting-permission-off", b.Session(S(ClaudeDeck.Core.Model.SessionState.Waiting, "Bash", ClaudeDeck.Core.Model.WaitReason.Permission), now, 500)),
            ("07-waiting-question", b.Session(S(ClaudeDeck.Core.Model.SessionState.Waiting, "AskUserQuestion", ClaudeDeck.Core.Model.WaitReason.Question) with { QuestionTitle = "Framework" }, now, 0)),
            ("08-done", b.Session(S(ClaudeDeck.Core.Model.SessionState.Done), now, 0)),
            ("09-compacting", b.Session(S(ClaudeDeck.Core.Model.SessionState.Compacting), now, 0)),
            ("10-error", b.Session(S(ClaudeDeck.Core.Model.SessionState.Error), now, 0)),
            ("11-hidden-waiting", b.Session(S(ClaudeDeck.Core.Model.SessionState.Working, "Edit"), now, 0, 2, 1)),
            ("12-empty", b.Session(null, now, 0)),
            ("13-five-hour", b.PlanWindow("SESSION", new(62.4, now.AddMinutes(135), now, "statusline"), now, false)),
            ("13b-session-credits", b.SessionWindow(new(100, now.AddMinutes(135), now, "statusline"), new ClaudeDeck.Core.Usage.UsageCredits(true, 50m, 12.40m, "EUR", now), now)),
            ("14-week-high", b.PlanWindow("SEMAINE", new(87, now.AddDays(2), now, "statusline"), now, true)),
            ("15-week-stale", b.PlanWindow("SEMAINE", new(41, now.AddDays(2), now.AddHours(-3), "statusline"), now, true)),
            ("16-plan-waiting", b.PlanWindow("SESSION", null, now, false)),
            ("17-api", b.Api(new(true, 41.27m, 3.1m, 118.4m, now, null), 5m, now)),
            ("18-api-low", b.Api(new(true, 41.27m, 3.1m, 3.4m, now, null), 5m, now)),
            ("19-api-unconfigured", b.Api(new(false, null, null, null, null, null), 5m, now)),
            ("20-api-error", b.Api(new(true, 12.5m, 1m, null, now, "clé refusée"), 5m, now)),
            ("21-test-pattern", ClaudeDeck.Core.Rendering.KeyViewBuilder.TestPattern(0)),
            ("22-logo", ClaudeDeck.Core.Rendering.KeyViewBuilder.Logo(null)),
        };
        foreach (var (name, view) in views)
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), ClaudeDeck.Core.Rendering.KeyRenderer.ToPng(r.Render(view)));

        // Animation « travaille » : une image toutes les 50 ms sur un cycle complet.
        var anim = Path.Combine(dir, "anim");
        Directory.CreateDirectory(anim);
        var working = S(ClaudeDeck.Core.Model.SessionState.Working, "Bash");
        for (var t = 0; t < 2000; t += 50)
            File.WriteAllBytes(Path.Combine(anim, $"working-{t:0000}.png"),
                ClaudeDeck.Core.Rendering.KeyRenderer.ToPng(r.Render(b.Session(working, now, t))));
        Console.WriteLine($"{views.Length} images écrites dans {dir}");
    }
}
