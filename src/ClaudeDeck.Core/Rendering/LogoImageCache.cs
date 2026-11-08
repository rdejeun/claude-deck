using ClaudeDeck.Core.Util;
using SkiaSharp;

namespace ClaudeDeck.Core.Rendering;

/// <summary>
/// Charge une image personnelle (PNG, JPG, BMP, WebP…) et la met à l'échelle d'une touche, centrée sur fond noir
/// en conservant ses proportions. Rechargée seulement si le chemin ou la date de modification changent.
/// </summary>
public sealed class LogoImageCache
{
    private readonly int _size;
    private readonly object _gate = new();
    private string? _path;
    private DateTime _stamp;
    private KeyImage? _image;
    private string? _failedFor;

    public LogoImageCache(int size) => _size = size;

    public KeyImage? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        lock (_gate)
        {
            DateTime stamp;
            try
            {
                if (!File.Exists(path)) return Fail(path, "fichier introuvable");
                stamp = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex)
            {
                return Fail(path, ex.Message);
            }
            if (path == _path && stamp == _stamp) return _image;

            _path = path;
            _stamp = stamp;
            _image = Load(path);
            if (_image is null) Fail(path, "format non reconnu");
            else _failedFor = null;
            return _image;
        }
    }

    private KeyImage? Fail(string path, string reason)
    {
        if (_failedFor != path) Log.Warn($"Image du logo inutilisable ({path}) : {reason}");
        _failedFor = path;
        _path = null;
        return null;
    }

    private KeyImage? Load(string path)
    {
        using var src = SKBitmap.Decode(path);
        if (src is null || src.Width == 0 || src.Height == 0) return null;

        var info = new SKImageInfo(_size, _size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var dst = new SKBitmap(info);
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.Black);
            var scale = Math.Min((float)_size / src.Width, (float)_size / src.Height);
            var w = src.Width * scale;
            var h = src.Height * scale;
            var rect = SKRect.Create((_size - w) / 2f, (_size - h) / 2f, w, h);
            using var image = SKImage.FromBitmap(src);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(image, rect, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        // Fond opaque : prémultiplié == non prémultiplié.
        return new KeyImage(_size, dst.Bytes);
    }
}
