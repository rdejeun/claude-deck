using System.Text.Json;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Install;
using ClaudeDeck.Core.Server;
using ClaudeDeck.Core.Util;
using Microsoft.Win32;

namespace ClaudeDeck.App;

/// <summary>
/// Installation « utilisateur » (aucun droit administrateur) :
/// copie de l'exécutable dans %LOCALAPPDATA%\ClaudeDeck, démarrage avec Windows (clé Run de HKCU),
/// hooks et status line dans ~/.claude/settings.json.
/// </summary>
internal sealed class AppInstaller : IHostActions
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClaudeDeck";

    private readonly ConfigStore _config;
    private readonly ClaudeSettingsInstaller _settings = new();

    public AppInstaller(ConfigStore config) => _config = config;

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeDeck");

    public static string InstalledExePath => Path.Combine(InstallDirectory, "claude-deck.exe");

    public static string CurrentExePath => Environment.ProcessPath ?? InstalledExePath;

    public static bool RunningFromInstallLocation =>
        string.Equals(Path.GetFullPath(CurrentExePath), Path.GetFullPath(InstalledExePath), StringComparison.OrdinalIgnoreCase);

    public bool IntegrationInstalled => _settings.IsInstalled();

    /// <summary>
    /// Commande de status line. Git Bash interprète les « \ » : on utilise des « / », et « ~ » quand c'est possible
    /// (il est développé après le découpage en mots, donc sans souci d'espaces dans le nom d'utilisateur).
    /// </summary>
    public static string StatusLineCommand()
    {
        var exe = Path.GetFullPath(InstalledExePath);
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
            CopySelf();
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
            SetAutostart(true);

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
            SetAutostart(false);
            var msg = "Intégration retirée de settings.json et démarrage automatique désactivé.";
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

    /// <summary>Copie l'exécutable courant à son emplacement d'installation (si on ne s'y exécute pas déjà).</summary>
    private static void CopySelf()
    {
        if (RunningFromInstallLocation) return;
        Directory.CreateDirectory(InstallDirectory);
        File.Copy(CurrentExePath, InstalledExePath, overwrite: true);
        Log.Info($"Exécutable copié vers {InstalledExePath}");
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
        if (enabled) key.SetValue(RunValue, $"\"{InstalledExePath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
