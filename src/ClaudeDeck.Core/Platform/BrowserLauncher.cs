namespace ClaudeDeck.Core.Platform;

/// <summary>
/// Construit la ligne de commande qui ouvre une adresse dans une NOUVELLE FENÊTRE du navigateur par défaut.
/// L'ouverture « classique » (ShellExecute) ouvre le plus souvent un onglet dans une fenêtre existante ;
/// on lance donc l'exécutable du navigateur avec son option dédiée.
/// </summary>
public static class BrowserLauncher
{
    private static readonly string[] Gecko = ["firefox", "waterfox", "librewolf", "floorp", "zen", "palemoon"];
    private static readonly string[] Chromium = ["chrome", "msedge", "brave", "vivaldi", "opera", "chromium", "thorium", "yandex", "arc"];

    /// <summary>Seules les adresses web sont acceptées (pas de file:, de commande ou de protocole arbitraire).</summary>
    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// À partir de la commande d'ouverture enregistrée pour le navigateur par défaut
    /// (ex. <c>"C:\Program Files\Mozilla Firefox\firefox.exe" -osint -url "%1"</c>), retourne l'exécutable et les
    /// arguments « nouvelle fenêtre », ou null si le navigateur n'est pas reconnu.
    /// </summary>
    public static (string Exe, IReadOnlyList<string> Args)? NewWindowCommand(string? openCommand, string url)
    {
        var exe = ExtractExecutable(openCommand);
        if (exe is null) return null;
        var name = Path.GetFileNameWithoutExtension(exe.Replace('\\', '/').Split('/')[^1]).ToLowerInvariant();
        if (Gecko.Contains(name)) return (exe, ["-new-window", url]);
        if (Chromium.Contains(name) || name.StartsWith("opera", StringComparison.Ordinal)) return (exe, ["--new-window", url]);
        return null;
    }

    /// <summary>Premier élément d'une ligne de commande Windows, entre guillemets ou non.</summary>
    internal static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        var exeIdx = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0) return c[..(exeIdx + 4)];
        var space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }
}
