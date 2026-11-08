using ClaudeDeck.Core.Config;
using ClaudeDeck.Core.Util;

namespace ClaudeDeck.Core.Rendering;

/// <summary>Fournit l'animation « travaille » choisie dans la configuration, chargée une seule fois.</summary>
public sealed class WorkingAnimationCache
{
    private readonly Lazy<SpriteAnimation?> _asterisk = new(() =>
    {
        try { return SpriteAnimation.LoadEmbeddedClaudeSpinner(); }
        catch (Exception ex)
        {
            Log.Error("Animation intégrée illisible", ex);
            return null;
        }
    });

    private string? _gifPath;
    private DateTime _gifStamp;
    private SpriteAnimation? _gif;

    public SpriteAnimation? Get(DisplayConfig display)
    {
        switch (display.WorkingAnimation)
        {
            case WorkingAnimations.Ring:
                return null;
            case WorkingAnimations.Gif when !string.IsNullOrWhiteSpace(display.WorkingAnimationPath):
                return LoadGif(Environment.ExpandEnvironmentVariables(display.WorkingAnimationPath.Trim().Trim('"'))) ?? _asterisk.Value;
            default:
                return _asterisk.Value;
        }
    }

    private SpriteAnimation? LoadGif(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (path == _gifPath && stamp == _gifStamp) return _gif;
            _gifPath = path;
            _gifStamp = stamp;
            _gif = SpriteAnimation.LoadGif(path);
            if (_gif is not null) Log.Info($"Animation chargée : {path} ({_gif.Frames.Count} images, {_gif.TotalMs} ms)");
            return _gif;
        }
        catch (Exception ex)
        {
            Log.Warn($"Animation {path} : {ex.Message}");
            return null;
        }
    }
}
