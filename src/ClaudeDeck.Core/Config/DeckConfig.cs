using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Config;

/// <summary>Configuration persistée dans <c>%APPDATA%\ClaudeDeck\config.json</c>. Ne contient aucun secret.</summary>
public sealed class DeckConfig
{
    /// <summary>Port HTTP local (127.0.0.1) sur lequel Claude Code envoie ses hooks.</summary>
    public int Port { get; set; } = 47777;

    /// <summary>Jeton partagé avec les hooks (en-tête X-ClaudeDeck-Token) : empêche une page web de forger des événements.</summary>
    public string Token { get; set; } = NewToken();

    public DisplayConfig Display { get; set; } = new();
    public DeviceConfig Device { get; set; } = new();
    public PlanUsageConfig PlanUsage { get; set; } = new();
    public ApiConfig Api { get; set; } = new();
    public StatusLineConfig StatusLine { get; set; } = new();

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

public sealed class DisplayConfig
{
    /// <summary>Luminosité normale (0-100).</summary>
    public int Brightness { get; set; } = 70;
    /// <summary>Luminosité en veille (0-100).</summary>
    public int DimBrightness { get; set; } = 10;
    /// <summary>Minutes sans activité avant la veille. 0 = jamais.</summary>
    public int DimAfterMinutes { get; set; } = 15;
    /// <summary>Éteint l'écran quand la session Windows est verrouillée.</summary>
    public bool OffWhenLocked { get; set; } = true;
    /// <summary>Minutes après lesquelles une session « terminée » non consultée repasse au repos. 0 = jamais.</summary>
    public int DoneFadeMinutes { get; set; } = 30;
    /// <summary>Images par seconde pour les animations (clignotement, activité).</summary>
    public int AnimationFps { get; set; } = 4;

    /// <summary>
    /// Contenu de la touche en bas à droite : <c>logo</c> (défaut) ou <c>api</c> (dépense via l'Admin API, qui exige
    /// un compte d'organisation Console).
    /// </summary>
    public string BottomRightKey { get; set; } = BottomRightKeys.Logo;

    /// <summary>Image personnelle (PNG, JPG…) affichée par la touche logo à la place du motif par défaut. Null = motif.</summary>
    public string? LogoImagePath { get; set; }

    /// <summary>Adresse ouverte par un appui sur la touche logo (http/https uniquement). Vide = aucune action.</summary>
    public string? LogoUrl { get; set; } = "https://claude.ai/new";

    /// <summary>
    /// Force une nouvelle fenêtre du navigateur par défaut (Firefox, Chrome, Edge, Brave, Vivaldi, Opera reconnus).
    /// Faux (défaut) : ouverture standard de Windows, en général un nouvel onglet.
    /// </summary>
    public bool LogoUrlNewWindow { get; set; } = false;
}

public static class BottomRightKeys
{
    public const string Logo = "logo";
    public const string Api = "api";
}

public sealed class DeviceConfig
{
    /// <summary>Identifiants produit USB acceptés (Vendor Elgato 0x0FD9). 0x0063 = Mini, 0x0090 = Mini MK.2.</summary>
    public List<string> ProductIds { get; set; } = ["0x0063", "0x0090"];

    /// <summary>
    /// Transformation appliquée avant l'envoi au périphérique (rotation anti-horaire en degrés puis miroirs),
    /// identique à la bibliothèque python-elgato-streamdeck. À ajuster si la mire de test apparaît tournée.
    /// </summary>
    public int Rotation { get; set; } = 90;
    public bool FlipHorizontal { get; set; } = false;
    public bool FlipVertical { get; set; } = true;

    [JsonIgnore]
    public IReadOnlyList<int> ParsedProductIds =>
        ProductIds.Select(p => p.Trim())
            .Select(p => p.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt32(p[2..], 16)
                : int.Parse(p, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
}

public sealed class PlanUsageConfig
{
    /// <summary>
    /// Interroge en plus l'endpoint OAuth NON DOCUMENTÉ utilisé par Claude Code (/api/oauth/usage) pour avoir les jauges
    /// même quand aucune session CLI ne tourne (ex. : extension VS Code seule). Désactivé par défaut.
    /// </summary>
    public bool OAuthFallback { get; set; } = false;
    public int OAuthPollMinutes { get; set; } = 5;
    /// <summary>Âge (minutes) au-delà duquel une jauge est affichée grisée comme « ancienne ».</summary>
    public int StaleAfterMinutes { get; set; } = 30;
}

public sealed class ApiConfig
{
    public int PollMinutes { get; set; } = 15;
    /// <summary>Solde saisi par l'utilisateur (USD), ou null.</summary>
    public decimal? BalanceUsd { get; set; }
    /// <summary>Instant de la saisie du solde.</summary>
    public DateTimeOffset? BalanceSetAt { get; set; }
    /// <summary>Dépense déjà comptée pour la journée UTC de la saisie, au moment de la saisie.</summary>
    public decimal BalanceBaselineUsd { get; set; }
    /// <summary>Seuil d'alerte (rouge) du solde estimé.</summary>
    public decimal LowBalanceUsd { get; set; } = 5m;
}

public sealed class StatusLineConfig
{
    /// <summary>Commande de status line préexistante, exécutée et affichée à la place du texte par défaut.</summary>
    public string? ChainCommand { get; set; }
    /// <summary>Objet statusLine d'origine (JSON brut), restauré à la désinstallation.</summary>
    public string? OriginalStatusLineJson { get; set; }
}

public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>Chargement / sauvegarde atomique de la configuration.</summary>
public sealed class ConfigStore
{
    private readonly object _gate = new();

    public ConfigStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "config.json");
        Current = Load();
    }

    public string Directory { get; }
    public string Path { get; }
    public DeckConfig Current { get; private set; }

    public event Action<DeckConfig>? Saved;

    /// <summary>Dossier par défaut : %APPDATA%\ClaudeDeck (Windows) ou ~/.config/ClaudeDeck.</summary>
    public static string DefaultDirectory() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeDeck");

    private DeckConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var cfg = JsonSerializer.Deserialize<DeckConfig>(File.ReadAllText(Path), ConfigJson.Options);
                if (cfg is not null)
                {
                    if (string.IsNullOrWhiteSpace(cfg.Token)) cfg.Token = DeckConfig.NewToken();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Configuration illisible ({Path}), valeurs par défaut utilisées", ex);
            try { File.Copy(Path, Path + ".invalide", overwrite: true); } catch { /* ignoré */ }
        }

        var fresh = new DeckConfig();
        Save(fresh);
        return fresh;
    }

    public void Update(Action<DeckConfig> mutate)
    {
        DeckConfig snapshot;
        lock (_gate)
        {
            mutate(Current);
            snapshot = Current;
            Save(snapshot);
        }
        Saved?.Invoke(snapshot);
    }

    private void Save(DeckConfig cfg)
    {
        lock (_gate)
        {
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, ConfigJson.Options));
            File.Move(tmp, Path, overwrite: true);
            Current = cfg;
        }
    }
}
