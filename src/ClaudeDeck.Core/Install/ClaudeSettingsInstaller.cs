using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Install;

public static class ClaudePaths
{
    /// <summary>Dossier de configuration de Claude Code : $CLAUDE_CONFIG_DIR ou ~/.claude.</summary>
    public static string ConfigDir()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    public static string UserSettingsPath() => Path.Combine(ConfigDir(), "settings.json");
}

/// <summary>
/// Ajoute / retire l'intégration ClaudeDeck dans ~/.claude/settings.json :
/// des hooks de type « http » vers le daemon local, et une status line qui lui transmet les limites du forfait.
/// Les autres réglages et hooks de l'utilisateur sont conservés ; une sauvegarde horodatée est faite avant écriture.
/// </summary>
public sealed class ClaudeSettingsInstaller
{
    public const string TokenHeader = "X-ClaudeDeck-Token";
    public const string HookPath = "/hook";

    /// <summary>Événements écoutés. Aucun « matcher » : un matcher absent signifie « tous ».</summary>
    public static readonly string[] HookEvents =
    [
        "SessionStart", "SessionEnd", "UserPromptSubmit",
        "PreToolUse", "PostToolUse", "PostToolUseFailure",
        "PermissionRequest", "PermissionDenied", "Notification",
        "Stop", "StopFailure", "SubagentStart", "SubagentStop",
        "PreCompact", "PostCompact", "PostModelSwitch",
    ];

    private readonly string _settingsPath;

    public ClaudeSettingsInstaller(string? settingsPath = null) => _settingsPath = settingsPath ?? ClaudePaths.UserSettingsPath();

    public string SettingsPath => _settingsPath;

    public sealed record Result(bool Changed, string? BackupPath, IReadOnlyList<string> Notes);

    public static string HookUrl(int port) => $"http://127.0.0.1:{port}{HookPath}";

    /// <param name="statusLineCommand">Commande qui lance <c>claude-deck.exe statusline</c>, ou null pour ne pas toucher à la status line.</param>
    public Result Install(DeckConfig config, string? statusLineCommand, out string? previousStatusLineJson)
    {
        var notes = new List<string>();
        var root = Load();
        previousStatusLineJson = null;

        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var evt in HookEvents)
        {
            var list = hooks[evt] as JsonArray;
            if (list is null)
            {
                list = new JsonArray();
                hooks[evt] = list;
            }
            RemoveOurs(list);
            list.Add(new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "http",
                        ["url"] = HookUrl(config.Port),
                        ["timeout"] = 3,
                        ["headers"] = new JsonObject { [TokenHeader] = config.Token },
                    },
                },
            });
        }

        if (statusLineCommand is not null)
        {
            var existing = root["statusLine"];
            if (existing is JsonObject sl && !IsOurStatusLine(sl))
            {
                previousStatusLineJson = sl.ToJsonString();
                notes.Add("Status line existante conservée : elle sera exécutée par ClaudeDeck et son texte affiché tel quel.");
            }
            root["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = statusLineCommand,
            };
        }

        if (root["disableAllHooks"]?.GetValueKind() == JsonValueKind.True)
            notes.Add("Attention : « disableAllHooks » est à true dans settings.json, les hooks ne seront pas exécutés.");

        var backup = Save(root);
        return new Result(true, backup, notes);
    }

    public Result Uninstall(string? originalStatusLineJson)
    {
        var notes = new List<string>();
        if (!File.Exists(_settingsPath)) return new Result(false, null, notes);
        var root = Load();

        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var (name, value) in hooks.ToList())
            {
                if (value is not JsonArray list) continue;
                RemoveOurs(list);
                if (list.Count == 0) hooks.Remove(name);
            }
            if (hooks.Count == 0) root.Remove("hooks");
        }

        if (root["statusLine"] is JsonObject sl && IsOurStatusLine(sl))
        {
            root.Remove("statusLine");
            if (!string.IsNullOrEmpty(originalStatusLineJson))
            {
                root["statusLine"] = JsonNode.Parse(originalStatusLineJson);
                notes.Add("Status line d'origine restaurée.");
            }
        }

        var backup = Save(root);
        return new Result(true, backup, notes);
    }

    /// <summary>
    /// Événements que ClaudeDeck écoute mais qui n'ont pas encore de hook ClaudeDeck (intégration installée par une
    /// version précédente). Vide si l'intégration est absente ou complète.
    /// </summary>
    public IReadOnlyList<string> MissingEvents()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return [];
            var root = Load();
            if (root["hooks"] is not JsonObject hooks || !hooks.Any(kv => kv.Value is JsonArray a && a.Any(IsOurEntry))) return [];
            return HookEvents.Where(e => hooks[e] is not JsonArray arr || !arr.Any(IsOurEntry)).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>L'intégration est-elle présente (au moins un hook ClaudeDeck) ?</summary>
    public bool IsInstalled()
    {
        if (!File.Exists(_settingsPath)) return false;
        try
        {
            var root = Load();
            return root["hooks"] is JsonObject hooks &&
                   hooks.Any(kv => kv.Value is JsonArray arr && arr.Any(IsOurEntry));
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveOurs(JsonArray list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (IsOurEntry(list[i])) list.RemoveAt(i);
        }
    }

    /// <summary>Une entrée est à nous si l'un de ses hooks porte l'en-tête de jeton ClaudeDeck.</summary>
    private static bool IsOurEntry(JsonNode? entry) =>
        entry is JsonObject o && o["hooks"] is JsonArray hs &&
        hs.Any(h => h is JsonObject ho && ho["headers"] is JsonObject headers && headers.ContainsKey(TokenHeader));

    private static bool IsOurStatusLine(JsonObject sl) =>
        sl["command"]?.GetValueKind() == JsonValueKind.String &&
        sl["command"]!.GetValue<string>().Contains("claude-deck", StringComparison.OrdinalIgnoreCase) &&
        sl["command"]!.GetValue<string>().Contains("statusline", StringComparison.OrdinalIgnoreCase);

    private JsonObject Load()
    {
        if (!File.Exists(_settingsPath)) return new JsonObject();
        var text = File.ReadAllText(_settingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonExt.ParseObject(text) ?? throw new InvalidDataException($"{_settingsPath} n'est pas un objet JSON.");
    }

    private string? Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        string? backup = null;
        if (File.Exists(_settingsPath))
        {
            backup = $"{_settingsPath}.claude-deck-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(_settingsPath, backup, overwrite: true);
        }
        var tmp = _settingsPath + ".claude-deck.tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // Conserve les accents et caractères spéciaux tels quels (pas d'échappement \uXXXX).
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        File.Move(tmp, _settingsPath, overwrite: true);
        Log.Info($"settings.json mis à jour ({_settingsPath}), sauvegarde : {backup ?? "aucune"}");
        return backup;
    }
}
