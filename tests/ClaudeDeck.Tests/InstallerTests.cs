using System.Text.Json.Nodes;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Install;

namespace ClaudeDeck.Tests;

public class InstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cd-inst-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public InstallerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Install_preserves_user_settings_and_is_idempotent()
    {
        File.WriteAllText(SettingsPath, """
            {
              // commentaire toléré
              "model": "opus",
              "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "mon-script.sh" } ] } ] },
              "statusLine": { "type": "command", "command": "~/.claude/statusline.sh" }
            }
            """);
        var cfg = new DeckConfig { Port = 47777, Token = "tok" };
        var inst = new ClaudeSettingsInstaller(SettingsPath);

        inst.Install(cfg, "~/AppData/Local/ClaudeDeck/claude-deck.exe statusline", out var previous);
        inst.Install(cfg, "~/AppData/Local/ClaudeDeck/claude-deck.exe statusline", out var previous2);

        Assert.Contains("statusline.sh", previous);
        Assert.Null(previous2); // la deuxième fois, la status line est déjà la nôtre

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count); // le hook utilisateur + le nôtre, sans doublon
        Assert.Equal("mon-script.sh", pre[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        var ours = pre[1]!["hooks"]![0]!;
        Assert.Equal("http", ours["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:47777/hook", ours["url"]!.GetValue<string>());
        Assert.Equal("tok", ours["headers"]![ClaudeSettingsInstaller.TokenHeader]!.GetValue<string>());
        foreach (var evt in ClaudeSettingsInstaller.HookEvents) Assert.NotNull(root["hooks"]![evt]);
        Assert.True(inst.IsInstalled());
        Assert.NotEmpty(Directory.GetFiles(_dir, "settings.json.claude-deck-*.bak"));

        inst.Uninstall(previous);
        var after = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Single(after["hooks"]!.AsObject());
        Assert.Single(after["hooks"]!["PreToolUse"]!.AsArray());
        Assert.Equal("~/.claude/statusline.sh", after["statusLine"]!["command"]!.GetValue<string>());
        Assert.False(inst.IsInstalled());
    }

    [Fact]
    public void Install_creates_settings_when_missing()
    {
        var inst = new ClaudeSettingsInstaller(SettingsPath);
        inst.Install(new DeckConfig(), null, out var previous);
        Assert.Null(previous);
        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Null(root["statusLine"]);
        Assert.True(inst.IsInstalled());
    }

    [Fact]
    public void Missing_events_are_detected_for_older_installs()
    {
        var inst = new ClaudeSettingsInstaller(SettingsPath);
        Assert.Empty(inst.MissingEvents()); // rien d'installé
        inst.Install(new DeckConfig(), null, out _);
        Assert.Empty(inst.MissingEvents());
        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        root["hooks"]!.AsObject().Remove("PostModelSwitch");
        File.WriteAllText(SettingsPath, root.ToJsonString());
        Assert.Equal(new[] { "PostModelSwitch" }, inst.MissingEvents());
    }
}
