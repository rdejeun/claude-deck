namespace ClaudeDeck.Core.Rendering;

/// <summary>Couleur RVB opaque.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

public enum KeyIcon
{
    None,
    Spinner,
    Check,
    Alert,
    Question,
    Compress,
    Cross,
    Idle,
    /// <summary>Invite de commande « > » (session au repos).</summary>
    Prompt,
    /// <summary>Grand motif d'étoile rayonnante (touche logo).</summary>
    Spark,
}

/// <summary>
/// Description purement visuelle d'une touche, indépendante du moteur de rendu.
/// Deux mises en page : « icône » (titre, icône, texte, sous-texte) et « jauge » (titre, grand nombre, barre, sous-texte),
/// choisie automatiquement selon que <see cref="Bar"/> est renseigné.
/// </summary>
public sealed record KeyView
{
    public Rgb Background { get; init; } = Rgb.Hex(0x000000);
    public Rgb Foreground { get; init; } = Rgb.Hex(0xFFFFFF);
    public Rgb Muted { get; init; } = Rgb.Hex(0x9CA3AF);
    public Rgb Accent { get; init; } = Rgb.Hex(0xFFFFFF);

    /// <summary>Ligne du haut (nom du projet, nom de la jauge).</summary>
    public string? Title { get; init; }
    /// <summary>Texte principal.</summary>
    public string? Main { get; init; }
    /// <summary>Texte principal en graisse normale (ex. nom du modèle) au lieu de gras.</summary>
    public bool MainRegular { get; init; }

    /// <summary>Ligne du bas en chiffres à largeur fixe (chrono : pas de décalage d'une seconde à l'autre).</summary>
    public bool SubMono { get; init; }

    /// <summary>Ligne du bas, discrète.</summary>
    public string? Sub { get; init; }

    public KeyIcon Icon { get; init; }
    /// <summary>Phase d'animation du spinner, en tours (0..1).</summary>
    public double SpinnerPhase { get; init; }

    /// <summary>Remplissage de la barre (0..1) : active la mise en page « jauge ».</summary>
    public double? Bar { get; init; }
    public Rgb BarColor { get; init; } = Rgb.Hex(0x22C55E);

    /// <summary>Pastille en haut à droite (ex. « +2 » sessions masquées).</summary>
    public string? Badge { get; init; }
    public Rgb BadgeColor { get; init; } = Rgb.Hex(0x6B7280);

    /// <summary>Petit bandeau de couleur en bas de la touche (ex. sessions masquées en attente).</summary>
    public Rgb? BottomStripe { get; init; }

    /// <summary>Mire de test : numéro de touche et repère d'orientation.</summary>
    public int? TestPatternIndex { get; init; }

    /// <summary>Image d'animation dessinée à la place de l'icône vectorielle (ex. astérisque de Claude Code).</summary>
    public Sprite? IconSprite { get; init; }

    /// <summary>Image fournie par l'utilisateur, affichée telle quelle sur toute la touche.</summary>
    public KeyImage? Image { get; init; }
}
