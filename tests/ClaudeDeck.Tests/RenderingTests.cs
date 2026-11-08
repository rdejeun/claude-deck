using ClaudeDeck.Core.Device;
using ClaudeDeck.Core.Model;
using ClaudeDeck.Core.Rendering;
using ClaudeDeck.Core.Usage;

namespace ClaudeDeck.Tests;

public class RenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bmp_has_valid_header_and_size()
    {
        var img = KeyImage.Solid(80, 255, 0, 0);
        var bmp = MiniImageEncoder.ToNativeBmp(img, 90, false, true);
        Assert.Equal(54 + 80 * 80 * 3, bmp.Length);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(80, BitConverter.ToInt32(bmp, 18));
        Assert.Equal(80, BitConverter.ToInt32(bmp, 22));
        Assert.Equal(24, BitConverter.ToInt16(bmp, 28));
        // Pixel BGR rouge pur.
        Assert.Equal(new byte[] { 0, 0, 255 }, bmp[54..57]);
        // 20 rapports de 1008 octets utiles pour une image.
        Assert.Equal(20, (int)Math.Ceiling(bmp.Length / 1008.0));
    }

    [Fact]
    public void Mini_orientation_is_a_transpose()
    {
        // Rotation 90° anti-horaire puis miroir vertical = transposition (x, y) → (y, x).
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 5; x++)
                Assert.Equal((y, x), MiniImageEncoder.SourceOf(x, y, 5, 90, false, true));
        Assert.Equal((2, 3), MiniImageEncoder.SourceOf(2, 3, 5, 0, false, false));
        Assert.Equal((2, 1), MiniImageEncoder.SourceOf(2, 3, 5, 180, false, false));
    }

    [Fact]
    public void Bmp_rows_are_bottom_up_after_transform()
    {
        // Image 80×80 noire avec un pixel blanc en (x=1, y=0) ; après transposition il est en (0, 1),
        // soit l'avant-dernière ligne stockée du BMP, première colonne.
        var rgba = new byte[80 * 80 * 4];
        for (var i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        var idx = (0 * 80 + 1) * 4;
        rgba[idx] = rgba[idx + 1] = rgba[idx + 2] = 255;
        var bmp = MiniImageEncoder.ToNativeBmp(new KeyImage(80, rgba), 90, false, true);
        var row = 80 - 1 - 1;
        Assert.Equal(255, bmp[54 + row * 240]);
        Assert.Equal(0, bmp[54 + (80 - 1) * 240 + 3]);
    }

    [Fact]
    public void Renderer_produces_opaque_images_for_every_state()
    {
        using var r = new KeyRenderer();
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        foreach (var state in Enum.GetValues<SessionState>())
        {
            var s = new SessionSnapshot("id", "projet", null, null, null, state, Now, WaitReason.Permission, "Bash", "Bash",
                Now.AddMinutes(-2), null, 0, 30, null, Now, null);
            var img = r.Render(b.Session(s, Now, 0, 4, 1, 1));
            Assert.Equal(80 * 80 * 4, img.Rgba.Length);
            for (var i = 3; i < img.Rgba.Length; i += 4) Assert.Equal(255, img.Rgba[i]);
        }
        var png = KeyRenderer.ToPng(r.Render(b.PlanWindow("SEMAINE", new RateWindow(40, Now.AddDays(1), Now, "x"), Now, true)));
        Assert.Equal(0x89, png[0]);
    }

    [Fact]
    public void Waiting_key_blinks()
    {
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        var s = new SessionSnapshot("id", "p", null, null, null, SessionState.Waiting, Now, WaitReason.Permission, null, null,
            null, null, 0, null, null, Now, null);
        Assert.NotEqual(b.Session(s, Now, 0, 4).Background, b.Session(s, Now, 2, 4).Background);
    }

    [Theory]
    [InlineData(4.123, "4,12 $")]
    [InlineData(128.4, "128 $")]
    [InlineData(1234, "1,2 k$")]
    [InlineData(25000, "25 k$")]
    public void Money_format_is_french(double usd, string expected) =>
        Assert.Equal(expected, KeyViewBuilder.Money((decimal)usd).Replace(' ', ' ').Replace(' ', ' '));

    [Fact]
    public void Durations_and_labels()
    {
        Assert.Equal("42 s", KeyViewBuilder.Duration(TimeSpan.FromSeconds(42)));
        Assert.Equal("4:05", KeyViewBuilder.Duration(TimeSpan.FromSeconds(245)));
        Assert.Equal("1 h 05", KeyViewBuilder.Duration(TimeSpan.FromMinutes(65)));
        Assert.Equal("Github", KeyViewBuilder.ToolLabel("mcp__github__create_issue"));
        Assert.Equal("Terminal", KeyViewBuilder.ToolLabel("Bash"));
    }

    [Fact]
    public void Reset_label_shows_day_for_weekly()
    {
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        Assert.Equal("15:00", b.ResetLabel(Now.AddHours(5), Now, weekly: false));
        Assert.StartsWith("jeu.", b.ResetLabel(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero), Now, weekly: true));
    }
}

public class LogoTests
{
    [Fact]
    public void Default_logo_is_drawn_spark()
    {
        var v = KeyViewBuilder.Logo(null);
        Assert.Equal(KeyIcon.Spark, v.Icon);
        using var r = new KeyRenderer();
        var img = r.Render(v);
        // Le centre-haut porte un rayon (couleur terracotta), le coin reste au fond.
        int Px(int x, int y) => (y * 80 + x) * 4;
        Assert.True(img.Rgba[Px(40, 18)] > 150);
        Assert.Equal(0x1C, img.Rgba[Px(1, 1)]);
    }

    [Fact]
    public void Custom_logo_is_loaded_scaled_and_cached()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cd-logo-{Guid.NewGuid():N}.png");
        try
        {
            // Image 200×100 rouge : mise à l'échelle 80×40, centrée verticalement sur fond noir.
            using (var bmp = new SkiaSharp.SKBitmap(200, 100))
            {
                bmp.Erase(SkiaSharp.SKColors.Red);
                using var data = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, data.ToArray());
            }
            var cache = new LogoImageCache(80);
            var img = cache.Get(path)!;
            Assert.NotNull(img);
            Assert.Same(img, cache.Get(path));
            Assert.Equal(255, img.Rgba[(40 * 80 + 40) * 4]);     // centre rouge
            Assert.Equal(0, img.Rgba[(5 * 80 + 40) * 4]);         // bande noire en haut
            using var r = new KeyRenderer();
            Assert.Same(img, r.Render(KeyViewBuilder.Logo(img)));
            Assert.Null(cache.Get(path + ".absent"));
            Assert.Null(cache.Get(null));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
