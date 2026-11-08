using System.Text.Json;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Install;
using ClaudeDeck.Core.Server;
using ClaudeDeck.Core.Util;
using Microsoft.Win32;

namespace ClaudeDeck.App;

/// <summary>
/// Intégration Claude Code : hooks et status line dans ~/.claude/settings.json, pointant vers l'exécutable
/// à son emplacement actuel. Volontairement, ClaudeDeck ne se recopie nulle part et ne s'inscrit au démarrage
/// de Windows que sur demande explicite (menu) : ces comportements automatiques ressemblent trop, pour un
/// antivirus, à ceux d'un logiciel malveillant.
/// </summary>
internal sealed class AppInstaller : IHostActions
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClaudeDeck";

    private readonly ConfigStore _config;
    private readonly ClaudeSettingsInstaller _settings = new();

    public AppInstaller(ConfigStore config) => _config = config;

    public static string CurrentExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "claude-deck.exe");

    public bool IntegrationInstalled => _settings.IsInstalled();

    /// <summary>
    /// Commande de status line. Git Bash interprète les « \ » : on utilise des « / », et « ~ » quand c'est possible
    /// (il est développé après le découpage en mots, donc sans souci d'espaces dans le nom d'utilisateur).
    /// </summary>
    public static string StatusLineCommand()
    {
        var exe = Path.GetFullPath(CurrentExePath);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (exe.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var rel = exe[(home.Length + 1)..].Replace('\\', '/');
            if (!rel.Contains(' ')) return $"~/{rel} statusline";
        }
        var fwd = exe.Replace('\\', '/');
        return fwd.Contains(' ') ? $"\"{fwd}\" statusline" : $"{fwd} statusline";
    }

    public string Install()
    {
        try
        {
            string? previous;
            var result = _settings.Install(_config.Current, StatusLineCommand(), out previous);
            if (previous is not null)
            {
                var chain = ReadCommand(previous);
                _config.Update(c =>
                {
                    c.StatusLine.ChainCommand = chain;
                    c.StatusLine.OriginalStatusLineJson = previous;
                });
            }
            // Si le démarrage automatique était actif, il suit l'exécutable s'il a été déplacé.
            if (AutostartEnabled) SetAutostart(true);

            var msg = $"Intégration installée dans {_settings.SettingsPath}.";
            if (result.BackupPath is not null) msg += $" Sauvegarde : {Path.GetFileName(result.BackupPath)}.";
            foreach (var n in result.Notes) msg += " " + n;
            msg += " Les sessions Claude Code déjà ouvertes la prendront en compte au prochain démarrage (ou après /hooks).";
            return msg;
        }
        catch (Exception ex)
        {
            Log.Error("Installation", ex);
            return $"Échec de l'installation : {ex.Message}";
        }
    }

    public string Uninstall()
    {
        try
        {
            var result = _settings.Uninstall(_config.Current.StatusLine.OriginalStatusLineJson);
            _config.Update(c =>
            {
                c.StatusLine.ChainCommand = null;
                c.StatusLine.OriginalStatusLineJson = null;
            });
            var msg = "Intégration retirée de settings.json.";
            foreach (var n in result.Notes) msg += " " + n;
            return msg;
        }
        catch (Exception ex)
        {
            Log.Error("Désinstallation", ex);
            return $"Échec de la désinstallation : {ex.Message}";
        }
    }

    private static string? ReadCommand(string statusLineJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(statusLineJson);
            return doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// L'intégration est installée mais la status line désigne un autre exécutable (fichier déplacé ou nouvelle version
    /// décompressée ailleurs) : il faut réinstaller l'intégration.
    /// </summary>
    public bool IntegrationPointsElsewhere()
    {
        try
        {
            if (!_settings.IsInstalled() || !File.Exists(_settings.SettingsPath)) return false;
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_settings.SettingsPath),
                documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var cmd = root?["statusLine"]?["command"]?.GetValue<string>();
            return cmd is not null && cmd.Contains("claude-deck", StringComparison.OrdinalIgnoreCase) && cmd != StatusLineCommand();
        }
        catch
        {
            return false;
        }
    }

    public static bool AutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{CurrentExePath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
