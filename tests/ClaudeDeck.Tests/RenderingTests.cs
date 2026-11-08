using ClaudeDeck.Core.Platform;
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
            var img = r.Render(b.Session(s, Now, 0, 1, 1));
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
        Assert.NotEqual(b.Session(s, Now, 0).Background, b.Session(s, Now, 500).Background);
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

public class BrowserLauncherTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\"", "C:\\Program Files\\Mozilla Firefox\\firefox.exe", "-new-window")]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --single-argument %1", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "--new-window")]
    [InlineData("\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --single-argument %1", "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", "--new-window")]
    [InlineData("C:\\Apps\\Brave\\brave.exe --single-argument %1", "C:\\Apps\\Brave\\brave.exe", "--new-window")]
    public void Known_browsers_get_new_window_flag(string command, string exe, string flag)
    {
        var r = BrowserLauncher.NewWindowCommand(command, "https://claude.ai/new");
        Assert.NotNull(r);
        Assert.Equal(exe, r!.Value.Exe);
        Assert.Equal(new[] { flag, "https://claude.ai/new" }, r.Value.Args);
    }

    [Fact]
    public void Unknown_browser_falls_back()
    {
        Assert.Null(BrowserLauncher.NewWindowCommand("\"C:\\x\\obscure.exe\" %1", "https://claude.ai/new"));
        Assert.Null(BrowserLauncher.NewWindowCommand(null, "https://claude.ai/new"));
    }

    [Theory]
    [InlineData("https://claude.ai/new", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("calc.exe", false)]
    [InlineData("", false)]
    public void Only_web_urls_are_allowed(string url, bool ok) => Assert.Equal(ok, BrowserLauncher.IsWebUrl(url));
}

public class LogoKeyTests
{
    private sealed class RecordingPlatform : IPlatformServices
    {
        public readonly List<(string Url, bool NewWindow)> Opened = [];
        public ClientProcess? ResolveClient(System.Net.IPEndPoint remote, int serverPort) => null;
        public bool IsAlive(ClientProcess client) => true;
        public bool FocusSession(SessionSnapshot session) => false;
        public bool IsElgatoSoftwareRunning() => false;
        public void OpenUrl(string url) { }
        public void OpenInBrowser(string url, bool newWindow) => Opened.Add((url, newWindow));
        public ISecretStore Secrets { get; } = new FileSecretStore(Path.GetTempPath(), null, null);
    }

    [Fact]
    public void Logo_key_opens_claude_even_when_dimmed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cd-logo-{Guid.NewGuid():N}");
        try
        {
            var time = new FakeTime();
            var store = new ClaudeDeck.Core.Config.ConfigStore(dir);
            var platform = new RecordingPlatform();
            using var deck = new ClaudeDeck.Core.Engine.DeckController(new ClaudeDeck.Core.Engine.SessionTracker(time),
                new PlanUsageStore(), null, store, null, platform, time);

            time.Advance(TimeSpan.FromHours(2)); // écran en veille
            deck.Press(ClaudeDeck.Core.Engine.DeckController.KeyBottomRight);
            Assert.Equal(("https://claude.ai/new", false), Assert.Single(platform.Opened));

            store.Update(c => c.Display.LogoUrl = null);
            deck.Press(ClaudeDeck.Core.Engine.DeckController.KeyBottomRight);
            Assert.Single(platform.Opened);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}


public class AnimationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Embedded_claude_spinner_loads_with_transparency()
    {
        var a = SpriteAnimation.LoadEmbeddedClaudeSpinner();
        Assert.Equal(21, a.Frames.Count);
        Assert.Equal(5, a.Frames.Distinct().Count()); // 5 glyphes partagés par les 21 étapes
        Assert.Equal(1400, a.TotalMs);
        Assert.Equal(24, a.Frames[0].Width);
        Assert.Equal(28, a.Frames[0].Height);
        // Coin transparent, glyphe opaque quelque part.
        Assert.Equal(0, a.Frames[0].Pixels[3]);
        Assert.Contains(a.Frames[0].Pixels.Where((_, i) => i % 4 == 3), alpha => alpha > 200);
    }

    [Fact]
    public void Frames_follow_their_own_durations_and_loop()
    {
        var f = Enumerable.Range(0, 3).Select(_ => new Sprite(1, 1, new byte[4])).ToList();
        var a = new SpriteAnimation(f, [100, 50, 200]);
        Assert.Same(f[0], a.FrameAt(0));
        Assert.Same(f[0], a.FrameAt(99));
        Assert.Same(f[1], a.FrameAt(100));
        Assert.Same(f[2], a.FrameAt(150));
        Assert.Same(f[2], a.FrameAt(349));
        Assert.Same(f[0], a.FrameAt(350));
        Assert.Same(f[1], a.FrameAt(350 * 10 + 120));
    }

    [Fact]
    public void Working_key_uses_animation_and_changes_over_time()
    {
        var b = new KeyViewBuilder(TimeZoneInfo.Utc) { WorkingAnimation = SpriteAnimation.LoadEmbeddedClaudeSpinner() };
        var s = new SessionSnapshot("id", "p", null, null, null, SessionState.Working, Now, WaitReason.None, "Bash", "Bash",
            Now, null, 0, null, null, Now, null);
        var v0 = b.Session(s, Now, 0);
        var v1 = b.Session(s, Now, 700);
        Assert.NotNull(v0.IconSprite);
        Assert.Equal(KeyIcon.None, v0.Icon);
        Assert.NotSame(v0.IconSprite, v1.IconSprite);
        Assert.Equal(v0, b.Session(s, Now, 30)); // même image → vue identique, pas de nouveau rendu

        using var r = new KeyRenderer();
        Assert.NotEqual(r.Render(v0).Hash, r.Render(v1).Hash);
    }

    [Fact]
    public void Gif_loader_reads_frames_and_durations()
    {
        var a = SpriteAnimation.LoadGif(Path.Combine(AppContext.BaseDirectory, "test-anim.gif"));
        Assert.NotNull(a);
        Assert.Equal(3, a!.Frames.Count);
        Assert.Equal(300, a.TotalMs);
        Assert.Null(SpriteAnimation.LoadGif("/inexistant.gif"));
    }

    [Fact]
    public void Old_config_is_migrated_to_smooth_animation()
    {
        var cfg = new ClaudeDeck.Core.Config.DeckConfig { SchemaVersion = 0 };
        cfg.Display.AnimationFps = 4;
        Assert.True(ClaudeDeck.Core.Config.ConfigStore.Migrate(cfg));
        Assert.Equal(12, cfg.Display.AnimationFps);
        Assert.False(ClaudeDeck.Core.Config.ConfigStore.Migrate(cfg));
    }
}
