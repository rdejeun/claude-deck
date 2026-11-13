using System.Reflection;
using SkiaSharp;

namespace ClaudeDeck.Core.Rendering;

/// <summary>Rend une <see cref="KeyView"/> en image de touche (SkiaSharp, police Inter embarquée).</summary>
public sealed class KeyRenderer : IDisposable
{
    private readonly SKTypeface _bold;
    private readonly SKTypeface _semi;
    private readonly SKTypeface _regular;
    private readonly int _size;

    public KeyRenderer(int size = 80)
    {
        _size = size;
        _bold = LoadFont("ClaudeDeck.Fonts.Inter-Bold.otf");
        _semi = LoadFont("ClaudeDeck.Fonts.Inter-SemiBold.otf");
        _regular = LoadFont("ClaudeDeck.Fonts.Inter-Regular.otf");
    }

    public int Size => _size;

    private static SKTypeface LoadFont(string resource)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                      ?? throw new InvalidOperationException($"Police manquante : {resource}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        return SKTypeface.FromStream(ms) ?? SKTypeface.Default;
    }

    public KeyImage Render(KeyView v)
    {
        if (v.Image is { } custom && custom.Size == _size) return custom;

        var info = new SKImageInfo(_size, _size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            // Toutes les coordonnées sont exprimées pour 80 px puis mises à l'échelle.
            var scale = _size / 80f;
            canvas.Scale(scale);
            canvas.Clear(ToSk(v.Background));

            if (v.TestPatternIndex is { } idx) DrawTestPattern(canvas, v, idx);
            else if (v.Icon == KeyIcon.Spark) DrawSpark(canvas, 40f, 40f, 27f, v.Accent);
            else if (v.Bar is not null) DrawGaugeLayout(canvas, v);
            else DrawIconLayout(canvas, v);

            if (v.BottomStripe is { } stripe)
            {
                using var p = Fill(stripe);
                canvas.DrawRect(0, 78, 80, 2, p);
            }
            if (v.Badge is { } badge) DrawBadge(canvas, badge, v.BadgeColor);
        }

        // Le fond est opaque : prémultiplié == non prémultiplié.
        var bytes = bitmap.Bytes;
        return new KeyImage(_size, bytes);
    }

    // ───────────── Mises en page ─────────────

    private void DrawIconLayout(SKCanvas c, KeyView v)
    {
        var titleRight = v.Badge is null ? 76f : 56f;
        if (v.Title is { } t)
        {
            // Centré s'il tient à taille normale, sinon aligné à gauche (réduit jusqu'à 10,5 px puis tronqué).
            using var probe = new SKFont(_semi, 12f);
            var fits = probe.MeasureText(t) <= titleRight - 4f;
            DrawText(c, t, _semi, 12f, 10.5f, 4f, titleRight, 13.5f, v.Foreground, fits ? SKTextAlign.Center : SKTextAlign.Left);
        }

        // Texte principal : gras par défaut ; graisse normale et plus petit pour le nom du modèle.
        var mainFace = v.MainRegular ? _regular : _bold;
        var mainSize = v.MainRegular ? 12f : 13.5f;
        // Nom du modèle rapproché du chrono, et animation/icône descendue d'autant : plus d'air autour.
        var mainBaseline = v.MainRegular ? 63f : 59f;
        var iconY = v.MainRegular ? 35f : 33f;
        if (v.IconSprite is { } sprite)
        {
            DrawSprite(c, sprite, 40f, iconY, 28f);
            if (v.Main is { } m) DrawText(c, m, mainFace, mainSize, 9f, 3f, 77f, mainBaseline, v.Foreground, SKTextAlign.Center);
        }
        else if (v.Icon != KeyIcon.None && v.Main is null)
        {
            // Icône seule (ex. « terminé ») : plus grande, à égale distance du titre (bas des lettres ≈ 16,5 px)
            // et de la ligne du bas (haut des lettres ≈ 67 px).
            DrawIcon(c, v.Icon, 40f, 41.5f, 15f, v.Accent, v.SpinnerPhase);
        }
        else if (v.Icon != KeyIcon.None)
        {
            DrawIcon(c, v.Icon, 40f, iconY, 10.5f, v.Accent, v.SpinnerPhase);
            if (v.Main is { } m) DrawText(c, m, mainFace, mainSize, 9f, 3f, 77f, mainBaseline, v.Foreground, SKTextAlign.Center);
        }
        else if (v.Main is { } m)
        {
            DrawText(c, m, _bold, 22f, 12f, 3f, 77f, 47f, v.Foreground, SKTextAlign.Center);
        }

        // Ligne du bas (chrono) au plus près du bord inférieur.
        if (v.Sub is { } sub)
        {
            if (v.SubMono) DrawTabular(c, sub, _semi, 11f, 40f, 77f, v.Muted); // 1 px plus bas que les autres lignes du bas
            else DrawText(c, sub, _semi, 11f, 8f, 3f, 77f, 76f, v.Muted, SKTextAlign.Center);
        }
    }

    private void DrawGaugeLayout(SKCanvas c, KeyView v)
    {
        if (v.Title is { } t) DrawText(c, t, _semi, 10.5f, 8f, 3f, 77f, 14f, v.Muted, SKTextAlign.Center);
        if (v.Main is { } m) DrawText(c, m, _bold, 22f, 13f, 3f, 77f, 41f, v.Foreground, SKTextAlign.Center);

        if (v.BarText is { } warning)
        {
            DrawWarning(c, warning, v.BarTextColor);
            if (v.Sub is { } s) DrawText(c, s, _semi, 11f, 8f, 3f, 77f, 72f, v.Muted, SKTextAlign.Center);
            return;
        }

        var bar = (float)Math.Clamp(v.Bar ?? 0, 0, 1);
        var rect = new SKRect(8, 49, 72, 56);
        using (var track = Fill(Rgb.Hex(0x2A2D35))) c.DrawRoundRect(rect, 3.5f, 3.5f, track);
        if (bar > 0)
        {
            var filled = new SKRect(rect.Left, rect.Top, rect.Left + Math.Max(rect.Height, rect.Width * bar), rect.Bottom);
            using var p = Fill(v.BarColor);
            c.DrawRoundRect(filled, 3.5f, 3.5f, p);
        }

        if (v.Sub is { } sub) DrawText(c, sub, _semi, 11f, 8f, 3f, 77f, 72f, v.Muted, SKTextAlign.Center);
    }

    private void DrawTestPattern(SKCanvas c, KeyView v, int index)
    {
        // Repère d'orientation : flèche vers le haut, coin rouge en haut à gauche.
        using (var red = Fill(Rgb.Hex(0xEF4444))) c.DrawRect(0, 0, 14, 14, red);
        using (var p = Fill(v.Foreground))
        {
            using var path = new SKPath();
            path.MoveTo(40, 4);
            path.LineTo(50, 16);
            path.LineTo(30, 16);
            path.Close();
            c.DrawPath(path, p);
        }
        DrawText(c, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), _bold, 34f, 20f, 3f, 77f, 54f, v.Foreground, SKTextAlign.Center);
        DrawText(c, "HAUT ↑", _semi, 10f, 8f, 3f, 77f, 72f, v.Muted, SKTextAlign.Center);
    }

    private void DrawBadge(SKCanvas c, string text, Rgb color)
    {
        using var font = new SKFont(_bold, 10.5f);
        var w = font.MeasureText(text) + 8;
        var rect = new SKRect(78 - w, 3, 78, 17);
        using (var bg = Fill(color)) c.DrawRoundRect(rect, 7, 7, bg);
        using var fg = Fill(Rgb.Hex(0xFFFFFF));
        c.DrawText(text, rect.MidX, 14f, SKTextAlign.Center, font, fg);
    }

    /// <summary>
    /// Dessine une image d'animation centrée, ajustée dans un carré de <paramref name="box"/> px.
    /// Les petites images (≤ box) sont dessinées à l'échelle 1, pixel pour pixel, pour rester nettes.
    /// </summary>
    private static void DrawSprite(SKCanvas c, Sprite s, float cx, float cy, float box)
    {
        var info = new SKImageInfo(s.Width, s.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(s.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var image = SKImage.FromPixels(pixmap);
            var scale = Math.Min(box / s.Width, box / s.Height);
            if (scale > 1f && s.Width >= box / 2) scale = 1f; // pas d'agrandissement flou des images déjà assez grandes
            var w = s.Width * scale;
            var h = s.Height * scale;
            // Alignement sur la grille de pixels à l'échelle 1.
            var x = scale == 1f ? MathF.Round(cx - w / 2) : cx - w / 2;
            var y = scale == 1f ? MathF.Round(cy - h / 2) : cy - h / 2;
            var sampling = scale == 1f ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell);
            using var paint = new SKPaint { IsAntialias = true };
            c.DrawImage(image, SKRect.Create(x, y, w, h), sampling, paint);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>« /!\ TEXTE » à la place de la barre d'une jauge : triangle d'avertissement puis texte, centrés.</summary>
    private void DrawWarning(SKCanvas c, string text, Rgb color)
    {
        using var font = new SKFont(_bold, 9.5f) { Subpixel = true, Edging = SKFontEdging.Antialias };
        const float tri = 9f, gap = 3f, maxWidth = 74f;
        while (font.Size > 7f && tri + gap + font.MeasureText(text) > maxWidth) font.Size -= 0.5f;
        var total = tri + gap + font.MeasureText(text);
        var x = 40f - total / 2f;
        const float baseline = 56f;

        using var p = Fill(color);
        using (var path = new SKPath())
        {
            path.MoveTo(x + tri / 2f, baseline - 8.5f);
            path.LineTo(x + tri, baseline);
            path.LineTo(x, baseline);
            path.Close();
            c.DrawPath(path, p);
        }
        using (var ink = Fill(Rgb.Hex(0x111318)))
        {
            c.DrawRect(x + tri / 2f - 0.6f, baseline - 6f, 1.2f, 3.3f, ink);
            c.DrawRect(x + tri / 2f - 0.6f, baseline - 1.9f, 1.2f, 1.2f, ink);
        }
        c.DrawText(text, x + tri + gap, baseline, SKTextAlign.Left, font, p);
    }

    // ───────────── Icônes vectorielles ─────────────

    /// <summary>Étoile rayonnante : rayons arrondis de longueurs alternées autour d'un centre.</summary>
    private static void DrawSpark(SKCanvas c, float cx, float cy, float r, Rgb color)
    {
        using var p = new SKPaint
        {
            Color = ToSk(color), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 6.2f, StrokeCap = SKStrokeCap.Round,
        };
        const int rays = 12;
        for (var i = 0; i < rays; i++)
        {
            var angle = (i * 360f / rays - 90f) * MathF.PI / 180f;
            var len = i % 2 == 0 ? r : r * 0.72f;
            var inner = r * 0.16f;
            c.DrawLine(cx + MathF.Cos(angle) * inner, cy + MathF.Sin(angle) * inner,
                       cx + MathF.Cos(angle) * len, cy + MathF.Sin(angle) * len, p);
        }
    }

    private static void DrawIcon(SKCanvas c, KeyIcon icon, float cx, float cy, float r, Rgb color, double phase)
    {
        using var stroke = new SKPaint
        {
            Color = ToSk(color), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.6f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };
        using var fill = Fill(color);

        switch (icon)
        {
            case KeyIcon.Spinner:
            {
                using var faint = new SKPaint
                {
                    Color = ToSk(color).WithAlpha(60), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f,
                };
                c.DrawCircle(cx, cy, r, faint);
                var start = (float)(phase * 360.0) - 90f;
                using var arc = new SKPath();
                arc.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), start, 100f);
                c.DrawPath(arc, stroke);
                break;
            }
            case KeyIcon.Check:
            {
                c.DrawCircle(cx, cy, r, stroke);
                using var path = new SKPath();
                path.MoveTo(cx - r * 0.45f, cy + r * 0.02f);
                path.LineTo(cx - r * 0.1f, cy + r * 0.38f);
                path.LineTo(cx + r * 0.5f, cy - r * 0.35f);
                c.DrawPath(path, stroke);
                break;
            }
            case KeyIcon.Alert:
            {
                using var tri = new SKPath();
                tri.MoveTo(cx, cy - r);
                tri.LineTo(cx + r * 1.1f, cy + r * 0.85f);
                tri.LineTo(cx - r * 1.1f, cy + r * 0.85f);
                tri.Close();
                c.DrawPath(tri, stroke);
                c.DrawLine(cx, cy - r * 0.35f, cx, cy + r * 0.2f, stroke);
                c.DrawCircle(cx, cy + r * 0.52f, 1.5f, fill);
                break;
            }
            case KeyIcon.Question:
            {
                c.DrawCircle(cx, cy, r, stroke);
                using var path = new SKPath();
                path.MoveTo(cx - r * 0.35f, cy - r * 0.25f);
                path.CubicTo(cx - r * 0.35f, cy - r * 0.75f, cx + r * 0.4f, cy - r * 0.75f, cx + r * 0.38f, cy - r * 0.22f);
                path.CubicTo(cx + r * 0.36f, cy + r * 0.05f, cx, cy + r * 0.05f, cx, cy + r * 0.3f);
                c.DrawPath(path, stroke);
                c.DrawCircle(cx, cy + r * 0.6f, 1.5f, fill);
                break;
            }
            case KeyIcon.Compress:
            {
                c.DrawLine(cx - r, cy - r * 0.6f, cx + r, cy - r * 0.6f, stroke);
                c.DrawLine(cx - r * 0.65f, cy, cx + r * 0.65f, cy, stroke);
                c.DrawLine(cx - r * 0.3f, cy + r * 0.6f, cx + r * 0.3f, cy + r * 0.6f, stroke);
                break;
            }
            case KeyIcon.Cross:
            {
                c.DrawCircle(cx, cy, r, stroke);
                var d = r * 0.4f;
                c.DrawLine(cx - d, cy - d, cx + d, cy + d, stroke);
                c.DrawLine(cx + d, cy - d, cx - d, cy + d, stroke);
                break;
            }
            case KeyIcon.Prompt:
            {
                using var chevron = new SKPath();
                chevron.MoveTo(cx - r * 0.45f, cy - r * 0.8f);
                chevron.LineTo(cx + r * 0.45f, cy);
                chevron.LineTo(cx - r * 0.45f, cy + r * 0.8f);
                c.DrawPath(chevron, stroke);
                break;
            }
            case KeyIcon.Idle:
            {
                using var thin = new SKPaint
                {
                    Color = ToSk(color), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f,
                };
                c.DrawCircle(cx, cy, r * 0.55f, thin);
                break;
            }
        }
    }

    // ───────────── Texte ─────────────

    /// <summary>Dessine un texte sur une ligne, en réduisant la taille jusqu'à <paramref name="minSize"/> puis en tronquant (…).</summary>
    private static void DrawText(SKCanvas c, string text, SKTypeface face, float size, float minSize,
        float left, float right, float baseline, Rgb color, SKTextAlign align)
    {
        var maxWidth = right - left;
        using var font = new SKFont(face, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        while (font.Size > minSize && font.MeasureText(text) > maxWidth) font.Size -= 0.5f;

        var shown = text;
        if (font.MeasureText(shown) > maxWidth)
        {
            while (shown.Length > 1 && font.MeasureText(shown + "…") > maxWidth) shown = shown[..^1];
            shown = shown.TrimEnd() + "…";
        }

        using var paint = Fill(color);
        var x = align switch
        {
            SKTextAlign.Center => (left + right) / 2f,
            SKTextAlign.Right => right,
            _ => left,
        };
        c.DrawText(shown, x, baseline, align, font, paint);
    }

    /// <summary>
    /// Chiffres « tabulaires » : chaque chiffre est centré dans une case de largeur fixe (celle du chiffre le plus large),
    /// avec la police normale (zéro simple). Le texte ne se décale plus quand les chiffres changent.
    /// </summary>
    private static void DrawTabular(SKCanvas c, string text, SKTypeface face, float size, float centerX, float baseline, Rgb color)
    {
        using var font = new SKFont(face, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var paint = Fill(color);
        var cell = "0123456789".Max(d => font.MeasureText(d.ToString()));
        float Width(char ch) => char.IsDigit(ch) ? cell : font.MeasureText(ch.ToString());
        var total = text.Sum(Width);
        var x = centerX - total / 2f;
        foreach (var ch in text)
        {
            var w = Width(ch);
            c.DrawText(ch.ToString(), x + w / 2f, baseline, SKTextAlign.Center, font, paint);
            x += w;
        }
    }

    private static SKPaint Fill(Rgb color) => new() { Color = ToSk(color), IsAntialias = true, Style = SKPaintStyle.Fill };

    private static SKColor ToSk(Rgb c) => new(c.R, c.G, c.B);

    /// <summary>Encode une image de touche en PNG (aperçu web).</summary>
    public static byte[] ToPng(KeyImage image)
    {
        var info = new SKImageInfo(image.Size, image.Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Rgba, 0, bmp.GetPixels(), image.Rgba.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose()
    {
        _bold.Dispose();
        _semi.Dispose();
        _regular.Dispose();
    }
}
