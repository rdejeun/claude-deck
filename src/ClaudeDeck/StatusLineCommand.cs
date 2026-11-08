using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Install;
using ClaudeDeck.Core.Server;

namespace ClaudeDeck.App;

/// <summary>
/// <c>claude-deck.exe statusline</c> : lancé par Claude Code à chaque mise à jour de la status line.
/// Transmet le JSON au daemon (limites du forfait, contexte, modèle), puis affiche soit la status line
/// préexistante de l'utilisateur (exécutée telle quelle), soit un texte compact par défaut.
/// Ne doit jamais échouer bruyamment ni bloquer : délais courts partout.
/// </summary>
internal static class StatusLineCommand
{
    public static int Run()
    {
        string input;
        using (var stdin = Console.OpenStandardInput())
        using (var reader = new StreamReader(stdin, new UTF8Encoding(false)))
        {
            input = reader.ReadToEnd();
        }

        DeckConfig cfg;
        try
        {
            cfg = new ConfigStore(ConfigStore.DefaultDirectory()).Current;
        }
        catch
        {
            cfg = new DeckConfig();
        }

        var daemonText = PostToDaemon(cfg, input);

        string output;
        if (!string.IsNullOrWhiteSpace(cfg.StatusLine.ChainCommand))
        {
            output = RunChained(cfg.StatusLine.ChainCommand!, input) ?? daemonText ?? LocalFormat(input);
        }
        else
        {
            output = daemonText ?? LocalFormat(input);
        }

        using var stdout = Console.OpenStandardOutput();
        var bytes = new UTF8Encoding(false).GetBytes(output);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
        return 0;
    }

    private static string? PostToDaemon(DeckConfig cfg, string json)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{cfg.Port}/statusline")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation(ClaudeSettingsInstaller.TokenHeader, cfg.Token);
            using var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var s = resp.Content.ReadAsStream();
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch
        {
            return null; // daemon arrêté : on affiche quand même quelque chose
        }
    }

    private static string LocalFormat(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return StatusLineFormatter.Format(doc.RootElement, DateTimeOffset.UtcNow);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Exécute la status line d'origine avec le même shell que Claude Code (Git Bash, sinon PowerShell).</summary>
    private static string? RunChained(string command, string input)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            var bash = FindGitBash();
            if (bash is not null)
            {
                psi.FileName = bash;
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);
            }

            using var p = Process.Start(psi);
            if (p is null) return null;
            p.StandardInput.Write(input);
            p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignoré */ }
                return null;
            }
            return outTask.Wait(1000) ? outTask.Result : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindGitBash()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
