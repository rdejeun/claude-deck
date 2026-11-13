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
        // Après 10 s d'attente, plus de clignotement : bleu vif fixe.
        var later = Now.AddSeconds(11);
        Assert.Equal(b.Session(s, later, 0).Background, b.Session(s, later, 500).Background);
        Assert.Equal(b.Session(s, Now, 0).Background, b.Session(s, later, 500).Background);
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
    public void Default_logo_is_the_embedded_image()
    {
        var v = KeyViewBuilder.Logo(null);
        Assert.NotNull(v.Image);
        Assert.Same(LogoImageCache.Default, v.Image);
        int Px(int x, int y) => (y * 80 + x) * 4;
        var img = v.Image!;
        Assert.Equal(0x11, img.Rgba[Px(1, 1)]);          // fond sombre
        Assert.Equal(255, img.Rgba[Px(1, 1) + 3]);       // opaque
        Assert.Contains(Enumerable.Range(0, 80 * 80), i => img.Rgba[i * 4] == 0xFF && img.Rgba[i * 4 + 1] == 0x63); // orange #FF6330
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
        Assert.Equal(14, a.Frames.Count);
        Assert.Equal(8, a.Frames.Distinct().Count()); // 8 glyphes partagés par les 14 étapes
        Assert.Equal(2000, a.TotalMs);
        Assert.Equal(26, a.Frames[0].Width);
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

public class StyleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static SessionSnapshot S(SessionState st, string? tool = null, string? model = "Opus 5.5") =>
        new("id", "p", null, null, model, st, Now, WaitReason.Permission, tool, tool, Now.AddSeconds(-30), null, 0, null, null, Now, null);

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-opus-5", "Opus 5")]
    [InlineData("claude-sonnet-4-5-20250929", "Sonnet 4.5")]
    [InlineData("claude-3-5-sonnet-20241022", "Sonnet 3.5")]
    [InlineData("claude-opus-4-6[1m]", "Opus 4.6")]
    [InlineData("us.anthropic.claude-haiku-4-5-20251001-v1:0", "Haiku 4.5")]
    [InlineData("claude-fable-5-1", "Fable 5.1")]
    [InlineData("opus", "Opus")]
    public void Model_ids_are_humanized(string id, string expected) => Assert.Equal(expected, ModelName.Format(id));

    [Fact]
    public void Working_always_shows_model_and_activity_below()
    {
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        var v = b.Session(S(SessionState.Working), Now, 0);
        Assert.Equal("Opus 5.5", v.Main);
        Assert.True(v.MainRegular);
        Assert.Equal("00:30", v.Sub);
        Assert.True(v.SubMono);
        var withTool = b.Session(S(SessionState.Working, "Bash"), Now, 0);
        Assert.Equal("Opus 5.5", withTool.Main);
        Assert.True(withTool.MainRegular);
        Assert.Equal("00:30", withTool.Sub); // l'activité n'est plus affichée
        Assert.Equal("Claude", b.Session(S(SessionState.Working, model: null), Now, 0).Main);
    }

    [Fact]
    public void Done_is_icon_only_and_idle_is_grey_prompt()
    {
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        var done = b.Session(S(SessionState.Done), Now, 0);
        Assert.Null(done.Main);
        Assert.Null(b.Session(S(SessionState.Waiting), Now, 0).Main); // « Autoriser » : icône seule
        Assert.Null(b.Session(S(SessionState.Waiting) with { WaitReason = WaitReason.Question }, Now, 0).Main);
        Assert.Equal(KeyIcon.Check, done.Icon);
        var idle = b.Session(S(SessionState.Idle) with { ContextUsedPercent = 34.4 }, Now, 0);
        Assert.Equal(KeyIcon.Prompt, idle.Icon);
        Assert.Equal("Opus 5.5", idle.Main);
        Assert.True(idle.MainRegular);
        Assert.Equal("contexte 34 %", idle.Sub);
        Assert.Equal("contexte ≈ 34 %", b.Session(S(SessionState.Idle) with { ContextUsedPercent = 34.4, ContextEstimated = true }, Now, 0).Sub);
        var bg = idle.Background;
        Assert.True(Math.Abs(bg.R - bg.B) <= 8 && Math.Abs(bg.R - bg.G) <= 8); // gris neutre
        using var r = new KeyRenderer();
        Assert.Equal(80 * 80 * 4, r.Render(done).Rgba.Length);
    }

    [Fact]
    public void Slots_are_out_of_phase()
    {
        var b = new KeyViewBuilder(TimeZoneInfo.Utc) { WorkingAnimation = SpriteAnimation.LoadEmbeddedClaudeSpinner() };
        var s = S(SessionState.Working);
        var cycle = b.WorkingAnimation!.TotalMs;
        var frames = Enumerable.Range(0, 3).Select(i => b.Session(s, Now, 0, phaseOffsetMs: i * cycle / 3).IconSprite).ToList();
        Assert.Equal(3, frames.Distinct().Count());
    }

    [Fact]
    public void State_colors_follow_the_palette()
    {
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        static bool Orangeish(Rgb c) => c.R > c.G && c.G >= c.B && c.R < 0x60;  // orange très sombre
        static bool Blueish(Rgb c) => c.B > c.R && c.B > c.G;
        static bool Greenish(Rgb c) => c.G > c.R && c.G > c.B;
        Assert.True(Orangeish(b.Session(S(SessionState.Working), Now, 0).Background));
        Assert.True(Blueish(b.Session(S(SessionState.Waiting), Now, 0).Background));
        Assert.True(Blueish(b.Session(S(SessionState.Waiting), Now, 500).Background));
        Assert.True(Greenish(b.Session(S(SessionState.Done), Now, 0).Background));
    }

    [Theory]
    [InlineData(10, 0x3B82F6)]
    [InlineData(89.9, 0x3B82F6)]
    [InlineData(90, 0xF97316)]
    [InlineData(94.9, 0xF97316)]
    [InlineData(95, 0xEF4444)]
    public void Gauge_colors(double pct, uint rgb)
    {
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        Assert.Equal(Rgb.Hex(rgb), b.PlanWindow("SESSION", new RateWindow(pct, Now.AddHours(1), Now, "x"), Now, false).BarColor);
    }

    [Fact]
    public void Spinner_cycle_is_two_seconds_with_equal_steps()
    {
        var a = SpriteAnimation.LoadEmbeddedClaudeSpinner();
        Assert.Equal(2000, a.TotalMs);
        var step = (double)a.TotalMs / a.Frames.Count; // 142,86 ms
        for (var i = 0; i < a.Frames.Count; i++) Assert.Same(a.Frames[i], a.FrameAt((long)(i * step + step / 2)));
    }
}


public class ChronoTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(5, "00:05")]
    [InlineData(252, "04:12")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(37230, "10:20:30")]
    public void Chrono_format(int seconds, string expected) => Assert.Equal(expected, KeyViewBuilder.Chrono(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Chrono_width_is_constant_in_monospace()
    {
        // Même largeur de texte pour deux secondes successives : seules les images des chiffres changent.
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        var now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        SessionSnapshot S(int sec) => new("id", "p", null, null, "Opus 5.5", SessionState.Working, now, WaitReason.None, null, null,
            now.AddSeconds(-sec), null, 0, null, null, now, null);
        using var r = new KeyRenderer();
        int Extent(KeyImage img)
        {
            // Colonnes occupées par la ligne du bas (y 68..79).
            var bg = img.Rgba.AsSpan(0, 3).ToArray();
            int min = 80, max = -1;
            for (var y = 68; y < 80; y++)
                for (var x = 0; x < 80; x++)
                {
                    var i = (y * 80 + x) * 4;
                    if (Math.Abs(img.Rgba[i] - bg[0]) + Math.Abs(img.Rgba[i + 1] - bg[1]) + Math.Abs(img.Rgba[i + 2] - bg[2]) > 60)
                    { min = Math.Min(min, x); max = Math.Max(max, x); }
                }
            return max - min;
        }
        // « 01:11 » (chiffres étroits) et « 08:08 » (chiffres larges) : même largeur.
        Assert.InRange(Extent(r.Render(b.Session(S(71), now, 0))) - Extent(r.Render(b.Session(S(488), now, 0))), -2, 2);
    }
}

public class MoneyTypographyTests
{
    [Theory]
    [InlineData("37,60 €", "37", ",60", "€")]
    [InlineData("~23,50 €", "~23", ",50", "€")]
    [InlineData("41,27 $", "41", ",27", "$")]
    [InlineData("12,00 CHF", "12", ",00", "CHF")]
    public void Splits_units_cents_currency(string text, string units, string cents, string currency)
    {
        var parts = KeyRenderer.SplitMoney(text.Replace(' ', ' '));
        Assert.NotNull(parts);
        Assert.Equal((units, cents, currency), parts!.Value);
    }

    [Theory]
    [InlineData("128 $")]
    [InlineData("62 %")]
    [InlineData("—")]
    public void Texts_without_cents_are_left_alone(string text) => Assert.Null(KeyRenderer.SplitMoney(text));

    [Fact]
    public void Money_flag_is_set_on_credit_balance()
    {
        var now = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var v = new KeyViewBuilder(TimeZoneInfo.Utc).SessionWindow(new RateWindow(100, now.AddHours(1), now, "x"),
            new UsageCredits(true, 50m, 12.4m, "EUR", now), now);
        Assert.True(v.MainMoney);
        using var r = new KeyRenderer();
        Assert.NotEqual(r.Render(v).Hash, r.Render(v with { MainMoney = false }).Hash);
    }
}


public class BackgroundTaskViewTests
{
    [Fact]
    public void Background_task_tile_shows_gear_shell_and_running_timer()
    {
        var now = new DateTimeOffset(2026, 11, 13, 23, 33, 0, TimeSpan.Zero);
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        var s = new SessionSnapshot("id", "download-receipt", null, null, "Opus 5.5", SessionState.BackgroundTask, now.AddMinutes(-12),
            WaitReason.None, null, null, now.AddMinutes(-14), null, 0, null, null, now, null);
        var v = b.Session(s, now, 0);
        Assert.Equal(KeyIcon.Gear, v.Icon);
        Assert.Equal("Shell", v.Main);
        Assert.True(v.MainRegular);
        Assert.Equal("14:00", v.Sub);
        var work = b.Session(s with { State = SessionState.Working }, now, 0);
        Assert.Equal(work.Background, v.Background); // mêmes couleurs que le travail
        Assert.NotEqual(v.SpinnerPhase, b.Session(s, now, 500).SpinnerPhase); // la roue tourne (1/16 de tour)
        using var r = new KeyRenderer();
        Assert.NotEqual(r.Render(v).Hash, r.Render(b.Session(s, now, 500)).Hash);
    }
}


public class CompactionViewTests
{
    [Fact]
    public void Compaction_tile_is_animated_and_harmonized()
    {
        var now = new DateTimeOffset(2026, 11, 13, 23, 52, 0, TimeSpan.Zero);
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        var s = new SessionSnapshot("id", "passavant", null, null, "Opus 5.5", SessionState.Compacting, now.AddSeconds(-95),
            WaitReason.None, null, null, now.AddMinutes(-5), null, 0, null, null, now, null);
        var v0 = b.Session(s, now, 0);
        var v1 = b.Session(s, now, 400);
        Assert.Equal(KeyIcon.Compress, v0.Icon);
        Assert.True(v0.MainRegular);
        Assert.True(v0.SubMono);
        Assert.Equal("01:35", v0.Sub);
        Assert.NotEqual(v0.SpinnerPhase, v1.SpinnerPhase);
        using var r = new KeyRenderer();
        Assert.NotEqual(r.Render(v0).Hash, r.Render(v1).Hash); // l'entonnoir s'anime
    }
}
