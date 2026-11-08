using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClaudeDeck.Core.Util;
using SkiaSharp;

namespace ClaudeDeck.Core.Rendering;

/// <summary>Petite image RGBA (prémultipliée) dessinée au centre d'une touche.</summary>
public sealed class Sprite
{
    public Sprite(int width, int height, byte[] rgbaPremul)
    {
        Width = width;
        Height = height;
        Pixels = rgbaPremul;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}

/// <summary>Animation image par image (durée propre à chaque image), jouée en boucle selon le temps écoulé.</summary>
public sealed class SpriteAnimation
{
    private readonly int[] _ends;

    public SpriteAnimation(IReadOnlyList<Sprite> frames, IReadOnlyList<int> durationsMs)
    {
        if (frames.Count == 0 || frames.Count != durationsMs.Count) throw new ArgumentException("Animation vide ou incohérente.");
        Frames = frames;
        _ends = new int[frames.Count];
        var t = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            t += Math.Max(20, durationsMs[i]); // les GIF à durée nulle sont joués à 50 i/s
            _ends[i] = t;
        }
        TotalMs = t;
    }

    public IReadOnlyList<Sprite> Frames { get; }
    public int TotalMs { get; }

    public Sprite FrameAt(long elapsedMs)
    {
        var t = (int)(((elapsedMs % TotalMs) + TotalMs) % TotalMs);
        var idx = Array.BinarySearch(_ends, t + 1);
        if (idx < 0) idx = ~idx;
        return Frames[Math.Min(idx, Frames.Count - 1)];
    }

    /// <summary>
    /// Astérisque animé de Claude Code, reconstitué à partir de glyphes PNG fournis par l'utilisateur :
    /// bande horizontale (un glyphe par case) + JSON décrivant la séquence des glyphes et la durée de chaque étape.
    /// Les étapes qui répètent un glyphe partagent la même image (aucun nouveau rendu entre elles).
    /// </summary>
    public static SpriteAnimation LoadEmbeddedClaudeSpinner()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var meta = asm.GetManifestResourceStream("ClaudeDeck.Animations.claude-spinner.json")
                         ?? throw new InvalidOperationException("Animation manquante.");
        using var doc = JsonDocument.Parse(meta);
        var root = doc.RootElement;
        var w = root.GetProperty("frameWidth").GetInt32();
        var h = root.GetProperty("frameHeight").GetInt32();
        var durations = root.GetProperty("durationsMs").EnumerateArray().Select(e => e.GetInt32()).ToList();

        using var png = asm.GetManifestResourceStream("ClaudeDeck.Animations.claude-spinner.png")
                        ?? throw new InvalidOperationException("Animation manquante.");
        using var strip = SKBitmap.Decode(png) ?? throw new InvalidDataException("Bande d'animation illisible.");
        var glyphCount = strip.Width / w;
        var glyphs = new List<Sprite>();
        for (var i = 0; i < glyphCount; i++)
        {
            using var frame = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var c = new SKCanvas(frame))
            {
                c.Clear(SKColors.Transparent);
                c.DrawBitmap(strip, SKRect.Create(i * w, 0, w, h), SKRect.Create(0, 0, w, h));
            }
            glyphs.Add(new Sprite(w, h, frame.Bytes));
        }

        var sequence = root.TryGetProperty("sequence", out var seq)
            ? seq.EnumerateArray().Select(e => e.GetInt32()).ToList()
            : Enumerable.Range(0, durations.Count).ToList();
        if (sequence.Count != durations.Count || sequence.Any(i => i < 0 || i >= glyphs.Count))
            throw new InvalidDataException("Séquence d'animation incohérente.");
        return new SpriteAnimation(sequence.Select(i => glyphs[i]).ToList(), durations);
    }

    /// <summary>Charge un GIF animé quelconque (images composées selon leurs dépendances), ou null en cas d'échec.</summary>
    public static SpriteAnimation? LoadGif(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null) return null;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var count = Math.Max(1, codec.FrameCount);
            var frameInfos = codec.FrameInfo;
            var decoded = new byte[count][];
            var frames = new List<Sprite>();
            var durations = new List<int>();
            for (var i = 0; i < count; i++)
            {
                using var bmp = new SKBitmap(info);
                var required = count > 1 ? frameInfos[i].RequiredFrame : -1;
                if (required >= 0 && decoded[required] is { } prior)
                    Marshal.Copy(prior, 0, bmp.GetPixels(), prior.Length);
                else
                    bmp.Erase(SKColors.Transparent);

                var result = codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(i, required));
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
                decoded[i] = bmp.Bytes;
                frames.Add(new Sprite(info.Width, info.Height, decoded[i]));
                durations.Add(count > 1 ? frameInfos[i].Duration : 1000);
            }
            return new SpriteAnimation(frames, durations);
        }
        catch (Exception ex)
        {
            Log.Warn($"Animation {path} illisible : {ex.Message}");
            return null;
        }
    }
}
