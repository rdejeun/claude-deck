using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ClaudeDeck.Core.Model;
using ClaudeDeck.Core.Platform;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.App;

/// <summary>Services Windows : identification du processus Claude Code, mise au premier plan, DPAPI.</summary>
internal sealed class WindowsPlatform : IPlatformServices
{
    private static readonly byte[] Entropy = "ClaudeDeck.v1"u8.ToArray();

    /// <summary>Processus système auxquels on cesse de remonter (leurs fenêtres ne sont pas « la » session).</summary>
    private static readonly HashSet<string> StopAt = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "svchost.exe", "services.exe", "wininit.exe", "winlogon.exe", "System",
        "sihost.exe", "RuntimeBroker.exe", "userinit.exe", "smss.exe", "csrss.exe",
    };

    public WindowsPlatform(string configDirectory)
    {
        Secrets = new FileSecretStore(configDirectory,
            protect: b => ProtectedData.Protect(b, Entropy, DataProtectionScope.CurrentUser),
            unprotect: b => ProtectedData.Unprotect(b, Entropy, DataProtectionScope.CurrentUser));
    }

    public ISecretStore Secrets { get; }

    public ClientProcess? ResolveClient(IPEndPoint remote, int serverPort)
    {
        var pid = Native.FindTcpClientPid(remote.Port, serverPort);
        if (pid is null) return null;
        try
        {
            using var p = Process.GetProcessById(pid.Value);
            DateTime? start = null;
            try { start = p.StartTime.ToUniversalTime(); } catch { /* accès refusé */ }
            return new ClientProcess(pid.Value, start, p.ProcessName);
        }
        catch
        {
            return new ClientProcess(pid.Value, null, null);
        }
    }

    public bool IsAlive(ClientProcess client)
    {
        try
        {
            using var p = Process.GetProcessById(client.Pid);
            if (p.HasExited) return false;
            if (client.StartTimeUtc is { } expected)
            {
                // PID réutilisé par un autre processus ?
                var actual = p.StartTime.ToUniversalTime();
                if (Math.Abs((actual - expected).TotalSeconds) > 2) return false;
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false; // n'existe plus
        }
        catch
        {
            return true; // accès refusé : dans le doute, on garde la session
        }
    }

    public bool FocusSession(SessionSnapshot session)
    {
        if (session.Client is null)
        {
            Log.Info($"Focus impossible pour {session.DisplayName} : processus client inconnu");
            return false;
        }

        var procs = Native.SnapshotProcesses();
        var windows = Native.TopLevelWindows();
        var self = Environment.ProcessId;

        // Remonte la chaîne des parents : claude → shell → terminal / VS Code.
        var pid = session.Client.Pid;
        var seen = new HashSet<int>();
        for (var depth = 0; depth < 16 && pid > 4 && seen.Add(pid); depth++)
        {
            if (!procs.TryGetValue(pid, out var entry)) break;
            if (StopAt.Contains(entry.ExeName) || pid == self) break;

            var candidates = windows.Where(w => w.Pid == pid).ToList();
            if (candidates.Count > 0)
            {
                // Plusieurs fenêtres (ex. VS Code) : on préfère celle dont le titre contient le nom du projet.
                var name = session.DisplayName;
                var best = candidates.FirstOrDefault(w => w.Title.Contains(name, StringComparison.OrdinalIgnoreCase));
                var target = best.Handle != IntPtr.Zero ? best : candidates[0];
                var ok = Native.Activate(target.Handle);
                Log.Info($"Focus {session.DisplayName} → « {target.Title} » ({entry.ExeName}#{pid}) : {(ok ? "ok" : "refusé par Windows")}");
                return ok;
            }

            // Garde-fou contre la réutilisation de PID : le parent doit être plus ancien que l'enfant.
            var parent = entry.ParentPid;
            if (!IsOlder(parent, pid)) break;
            pid = parent;
        }

        Log.Info($"Aucune fenêtre trouvée pour {session.DisplayName} (pid {session.Client.Pid})");
        return false;
    }

    private static bool IsOlder(int parent, int child)
    {
        try
        {
            using var p = Process.GetProcessById(parent);
            using var c = Process.GetProcessById(child);
            return p.StartTime <= c.StartTime;
        }
        catch
        {
            return true; // accès refusé : on tente quand même
        }
    }

    public bool IsElgatoSoftwareRunning()
    {
        var procs = Process.GetProcessesByName("StreamDeck");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Ouverture de {url} impossible : {ex.Message}");
        }
    }
}
