using System.Net;
using System.Text;
using ClaudeDeck.Core.Model;

namespace ClaudeDeck.Core.Platform;

/// <summary>Services dépendant du système d'exploitation. Implémentés pour Windows dans l'application principale.</summary>
public interface IPlatformServices
{
    /// <summary>Identifie le processus à l'origine d'une connexion TCP locale (le processus Claude Code qui envoie le hook).</summary>
    ClientProcess? ResolveClient(IPEndPoint remote, int serverPort);

    /// <summary>Le processus client existe-t-il toujours ? (null = inconnu, on ne supprime pas la session).</summary>
    bool IsAlive(ClientProcess client);

    /// <summary>Met au premier plan la fenêtre qui héberge la session.</summary>
    bool FocusSession(SessionSnapshot session);

    /// <summary>Le logiciel Stream Deck d'Elgato est-il lancé (il se disputerait le périphérique) ?</summary>
    bool IsElgatoSoftwareRunning();

    void OpenUrl(string url);

    ISecretStore Secrets { get; }
}

/// <summary>Stockage d'un secret (clé Admin API).</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string? value);
}

/// <summary>Implémentation neutre (simulateur, tests). Les secrets sont stockés en clair dans un fichier du dossier de config.</summary>
public sealed class NullPlatform : IPlatformServices
{
    public NullPlatform(string configDirectory) => Secrets = new FileSecretStore(configDirectory, protect: null, unprotect: null);

    public ClientProcess? ResolveClient(IPEndPoint remote, int serverPort) => null;
    public bool IsAlive(ClientProcess client) => true;
    public bool FocusSession(SessionSnapshot session)
    {
        Util.Log.Info($"[sim] focus demandé pour la session {session.DisplayName} ({session.Cwd})");
        return false;
    }
    public bool IsElgatoSoftwareRunning() => false;
    public void OpenUrl(string url) => Util.Log.Info($"[sim] ouvrir {url}");
    public ISecretStore Secrets { get; }
}

/// <summary>Secrets stockés dans un fichier, éventuellement chiffrés (DPAPI sous Windows).</summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly Func<byte[], byte[]>? _protect;
    private readonly Func<byte[], byte[]>? _unprotect;
    private readonly object _gate = new();

    public FileSecretStore(string directory, Func<byte[], byte[]>? protect, Func<byte[], byte[]>? unprotect)
    {
        _directory = directory;
        _protect = protect;
        _unprotect = unprotect;
    }

    private string PathFor(string name) => Path.Combine(_directory, $"secret-{name}.bin");

    public string? Get(string name)
    {
        lock (_gate)
        {
            var path = PathFor(name);
            if (!File.Exists(path)) return null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (_unprotect is not null) bytes = _unprotect(bytes);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                Util.Log.Error($"Secret {name} illisible", ex);
                return null;
            }
        }
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            var path = PathFor(name);
            if (string.IsNullOrEmpty(value))
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(value);
            if (_protect is not null) bytes = _protect(bytes);
            Directory.CreateDirectory(_directory);
            File.WriteAllBytes(path, bytes);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
