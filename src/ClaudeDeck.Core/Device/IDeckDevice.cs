using ClaudeDeck.Core.Rendering;

namespace ClaudeDeck.Core.Device;

/// <summary>Un périphérique à touches LCD (Stream Deck Mini réel ou simulé).</summary>
public interface IDeckDevice : IDisposable
{
    string Description { get; }
    int KeyCount { get; }
    int Columns { get; }
    int KeyPixelSize { get; }

    void SetKeyImage(int key, KeyImage image);
    void SetBrightness(int percent);

    /// <summary>Changement d'état d'une touche : index (0 = en haut à gauche), enfoncée ?</summary>
    event Action<int, bool>? KeyStateChanged;

    /// <summary>Le périphérique a été débranché ou une erreur d'E/S est survenue.</summary>
    event Action? Disconnected;
}
