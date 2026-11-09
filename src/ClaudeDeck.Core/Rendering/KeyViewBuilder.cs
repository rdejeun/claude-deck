using System.Globalization;
using ClaudeDeck.Core.Model;
using ClaudeDeck.Core.Usage;

namespace ClaudeDeck.Core.Rendering;

/// <summary>Traduit l'état métier (sessions, jauges) en <see cref="KeyView"/>. Sans dépendance au rendu : testable.</summary>
public sealed class KeyViewBuilder
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private readonly TimeZoneInfo _zone;

    public KeyViewBuilder(TimeZoneInfo? zone = null) => _zone = zone ?? TimeZoneInfo.Local;

    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);

    // Palette (fond sombre : lisible sur les petits LCD, peu éblouissant).
    private static class C
    {
        public static readonly Rgb Black = Rgb.Hex(0x000000);
        // Au repos : gris neutre.
        public static readonly Rgb IdleBg = Rgb.Hex(0x1E1F23), IdleFg = Rgb.Hex(0xC9CBD0), IdleAccent = Rgb.Hex(0x9A9DA5), IdleMuted = Rgb.Hex(0x8A8D94);
        // Travaille : orange très sombre et saturé.
        public static readonly Rgb WorkBg = Rgb.Hex(0x3A1300), WorkFg = Rgb.Hex(0xFFE9DD), WorkAccent = Rgb.Hex(0xFF7A3D), WorkMuted = Rgb.Hex(0xFFB088);
        // En attente d'une réponse : bleu, clignotant (vif / sombre).
        public static readonly Rgb WaitOnBg = Rgb.Hex(0x1D5FE0), WaitOnFg = Rgb.Hex(0xFFFFFF);
        public static readonly Rgb WaitOffBg = Rgb.Hex(0x0B1F4A), WaitOffFg = Rgb.Hex(0xBFD4FF), WaitOffAccent = Rgb.Hex(0x60A5FA);
        public static readonly Rgb DoneBg = Rgb.Hex(0x0E3B23), DoneFg = Rgb.Hex(0xDCFCE7), DoneAccent = Rgb.Hex(0x4ADE80);
        public static readonly Rgb CompactBg = Rgb.Hex(0x2E1065), CompactFg = Rgb.Hex(0xEDE9FE), CompactAccent = Rgb.Hex(0xA78BFA);
        public static readonly Rgb ErrBg = Rgb.Hex(0x450A0A), ErrFg = Rgb.Hex(0xFEE2E2), ErrAccent = Rgb.Hex(0xF87171);
        public static readonly Rgb GaugeBg = Rgb.Hex(0x111318), GaugeFg = Rgb.Hex(0xF3F4F6), GaugeMuted = Rgb.Hex(0x9CA3AF);
        public static readonly Rgb StaleFg = Rgb.Hex(0x6B7280), StaleBar = Rgb.Hex(0x4B5563);
        public static readonly Rgb Green = Rgb.Hex(0x22C55E), Amber = Rgb.Hex(0xF59E0B), Red = Rgb.Hex(0xEF4444);
        public static readonly Rgb LowBalanceBg = Rgb.Hex(0x3B0A0A);
        public static readonly Rgb Orange = Rgb.Hex(0xF97316);
        public static readonly Rgb Blue = Rgb.Hex(0x3B82F6);
    }

    // ───────────────────────────── Sessions ─────────────────────────────

    /// <summary>Durée du clignotement d'une touche en attente (permission, question).</summary>
    public static readonly TimeSpan BlinkDuration = TimeSpan.FromSeconds(10);

    /// <summary>Animation de l'état « travaille ». Null = anneau vectoriel.</summary>
    public SpriteAnimation? WorkingAnimation { get; set; }

    /// <param name="animMs">Horloge d'animation en millisecondes (clignotement, spinner, GIF).</param>
    /// <param name="phaseOffsetMs">Décalage propre à la touche, pour que plusieurs sessions ne s'animent pas en phase.</param>
    public KeyView Session(SessionSnapshot? s, DateTimeOffset now, long animMs, int hiddenCount = 0, int hiddenWaiting = 0, long phaseOffsetMs = 0)
    {
        KeyView view;
        if (s is null)
        {
            view = new KeyView { Background = C.Black, Foreground = Rgb.Hex(0x3F3F46), Muted = Rgb.Hex(0x3F3F46), Main = "·" };
        }
        else
        {
            var title = s.DisplayName;
            // Clignotement à 1 Hz pendant les 10 premières secondes d'une attente, puis bleu vif fixe.
            var blinkOn = now - s.StateSince >= BlinkDuration || animMs / 500 % 2 == 0;
            var phase = (animMs + phaseOffsetMs) % 1000 / 1000.0;
            var sprite = WorkingAnimation?.FrameAt(animMs + phaseOffsetMs);
            view = s.State switch
            {
                SessionState.Working => new KeyView
                {
                    Background = C.WorkBg, Foreground = C.WorkFg, Accent = C.WorkAccent, Muted = C.WorkMuted,
                    Title = title,
                    Icon = sprite is null ? KeyIcon.Spinner : KeyIcon.None,
                    SpinnerPhase = sprite is null ? phase : 0,
                    IconSprite = sprite,
                    // Le modèle (graisse normale) pendant tout le travail ; l'activité et le chrono en dessous.
                    Main = s.Model ?? "Claude",
                    MainRegular = true,
                    Sub = s.TurnStartedAt is { } t ? Duration(now - t) : null,
                },
                SessionState.Waiting => new KeyView
                {
                    Background = blinkOn ? C.WaitOnBg : C.WaitOffBg,
                    Foreground = blinkOn ? C.WaitOnFg : C.WaitOffFg,
                    Accent = blinkOn ? C.WaitOnFg : C.WaitOffAccent,
                    Muted = blinkOn ? C.WaitOnFg : C.WaitOffFg,
                    Title = title,
                    // Pas de légende : l'icône (triangle = permission, « ? » = question) suffit.
                    Icon = s.WaitReason == WaitReason.Question ? KeyIcon.Question : KeyIcon.Alert,
                    Sub = s.WaitReason != WaitReason.Question && s.CurrentTool is { } tool ? ToolLabel(tool) : Duration(now - s.StateSince),
                },
                SessionState.Done => new KeyView
                {
                    Background = C.DoneBg, Foreground = C.DoneFg, Accent = C.DoneAccent, Muted = Rgb.Hex(0x86EFAC),
                    Title = title, Icon = KeyIcon.Check,
                    Sub = Ago(now - s.StateSince),
                },
                SessionState.Compacting => new KeyView
                {
                    Background = C.CompactBg, Foreground = C.CompactFg, Accent = C.CompactAccent, Muted = Rgb.Hex(0xC4B5FD),
                    Title = title, Icon = KeyIcon.Compress, Main = "Compaction",
                    Sub = Duration(now - s.StateSince),
                },
                SessionState.Error => new KeyView
                {
                    Background = C.ErrBg, Foreground = C.ErrFg, Accent = C.ErrAccent, Muted = Rgb.Hex(0xFCA5A5),
                    Title = title, Icon = KeyIcon.Cross, Main = "Erreur API",
                    Sub = Ago(now - s.StateSince),
                },
                _ => new KeyView
                {
                    Background = C.IdleBg, Foreground = C.IdleFg, Accent = C.IdleAccent, Muted = C.IdleMuted,
                    Title = title, Icon = KeyIcon.Prompt,
                    Main = s.Model ?? "Claude", MainRegular = true,
                    Sub = s.ContextUsedPercent is { } ctx ? $"contexte {(s.ContextEstimated ? "≈ " : "")}{ctx:0} %" : null,
                },
            };
        }

        if (hiddenCount > 0)
        {
            view = view with
            {
                Badge = $"+{hiddenCount}",
                BadgeColor = hiddenWaiting > 0 ? C.Blue : Rgb.Hex(0x4B5563),
                BottomStripe = hiddenWaiting > 0 ? C.Blue : null,
            };
        }
        return view;
    }

    /// <summary>Nom d'outil court et lisible.</summary>
    public static string ToolLabel(string tool)
    {
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = tool.Split("__", StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? Capitalize(parts[1]) : "MCP";
        }
        return tool switch
        {
            "Bash" or "BashOutput" or "KillShell" or "PowerShell" => "Terminal",
            "Read" => "Lecture",
            "Edit" or "MultiEdit" or "NotebookEdit" => "Édition",
            "Write" => "Écriture",
            "Grep" or "Glob" or "LS" => "Recherche",
            "WebFetch" => "Web",
            "WebSearch" => "Web",
            "Task" or "Agent" => "Agent",
            "TodoWrite" or "TaskCreate" or "TaskUpdate" => "Tâches",
            "AskUserQuestion" => "Question",
            "ExitPlanMode" => "Plan",
            "Skill" => "Skill",
            _ => tool,
        };
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Durée compacte : « 42 s », « 4:12 », « 1 h 05 ».</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds} s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}:{d.Seconds:00}";
        return $"{(int)d.TotalHours} h {d.Minutes:00}";
    }

    /// <summary>« à l'instant », « il y a 3 min », « il y a 2 h ».</summary>
    public static string Ago(TimeSpan d)
    {
        if (d.TotalSeconds < 45) return "à l'instant";
        if (d.TotalMinutes < 60) return $"il y a {Math.Max(1, (int)Math.Round(d.TotalMinutes))} min";
        if (d.TotalHours < 48) return $"il y a {(int)d.TotalHours} h";
        return $"il y a {(int)d.TotalDays} j";
    }

    // ───────────────────────────── Jauges forfait ─────────────────────────────

    public KeyView PlanWindow(string title, RateWindow? w, DateTimeOffset now, bool weekly)
    {
        var baseView = new KeyView
        {
            Background = C.GaugeBg, Foreground = C.GaugeFg, Muted = C.GaugeMuted, Title = title,
        };

        if (w is null)
            return baseView with { Main = "—", Bar = 0, BarColor = C.StaleBar, Sub = "en attente", Foreground = C.StaleFg };

        if (w.HasReset(now))
            return baseView with { Main = "—", Bar = 0, BarColor = C.StaleBar, Sub = "réinitialisé", Foreground = C.StaleFg };

        var stale = now - w.ObservedAt > StaleAfter;
        var pct = w.UsedPercent;
        // Bleu jusqu'à 90 %, orange jusqu'à 95 %, rouge au-delà.
        var color = stale ? C.StaleBar : pct >= 95 ? C.Red : pct >= 90 ? C.Orange : C.Blue;
        return baseView with
        {
            Main = $"{(stale ? "~" : "")}{pct:0} %",
            Bar = Math.Clamp(pct / 100.0, 0, 1),
            BarColor = color,
            Foreground = stale ? C.StaleFg : C.GaugeFg,
            Sub = w.ResetsAt is { } r ? "→ " + ResetLabel(r, now, weekly) : null,
        };
    }

    /// <summary>Heure locale de réinitialisation ; avec le jour si ce n'est pas aujourd'hui.</summary>
    public string ResetLabel(DateTimeOffset resetsAt, DateTimeOffset now, bool weekly)
    {
        var local = TimeZoneInfo.ConvertTime(resetsAt, _zone);
        var localNow = TimeZoneInfo.ConvertTime(now, _zone);
        var time = local.ToString("HH:mm", Fr);
        if (local.Date == localNow.Date && !weekly) return time;
        if (local.Date == localNow.Date) return $"auj. {time}";
        if (local.Date == localNow.Date.AddDays(1) && !weekly) return $"dem. {time}";
        var day = local.ToString("ddd", Fr); // « jeu. »
        return $"{day} {time}";
    }

    // ───────────────────────────── API ─────────────────────────────

    public KeyView Api(ApiSpendSnapshot api, decimal lowBalanceUsd, DateTimeOffset now)
    {
        var view = new KeyView { Background = C.GaugeBg, Foreground = C.GaugeFg, Muted = C.GaugeMuted, Title = "API · 7 J" };
        if (!api.Configured)
            return view with { Main = "—", Sub = "clé à saisir", Foreground = C.StaleFg };

        if (api.Spend7dUsd is null)
            return view with { Main = "…", Sub = api.Error ?? "chargement", Foreground = C.StaleFg };

        var stale = api.FetchedAt is { } f && now - f > StaleAfter + StaleAfter;
        var low = api.BalanceEstimateUsd is { } b && b <= lowBalanceUsd;
        string sub;
        if (api.Error is not null) sub = api.Error;
        else if (api.BalanceEstimateUsd is { } bal) sub = $"solde ≈ {Money(bal)}";
        else sub = $"auj. {Money(api.TodayUsd ?? 0)}";

        return view with
        {
            Main = Money(api.Spend7dUsd.Value),
            Sub = sub,
            Background = low ? C.LowBalanceBg : C.GaugeBg,
            Foreground = stale ? C.StaleFg : C.GaugeFg,
            Muted = low ? Rgb.Hex(0xFCA5A5) : C.GaugeMuted,
        };
    }

    /// <summary>Montant en dollars au format français : « 4,12 $ », « 128 $ », « 1,2 k$ ».</summary>
    public static string Money(decimal usd)
    {
        if (usd < 0) return "−" + Money(-usd);
        if (usd >= 10_000) return (usd / 1000m).ToString("0", Fr) + " k$";
        if (usd >= 1_000) return (usd / 1000m).ToString("0.0", Fr) + " k$";
        if (usd >= 100) return usd.ToString("0", Fr) + " $";
        return usd.ToString("0.00", Fr) + " $";
    }

    // ───────────────────────────── Divers ─────────────────────────────

    /// <summary>
    /// Touche logo : image personnelle si fournie, sinon le logo par défaut embarqué (assets/logo/default-logo.png),
    /// et en dernier recours un motif vectoriel.
    /// </summary>
    public static KeyView Logo(KeyImage? custom) => (custom ?? LogoImageCache.Default) is { } image
        ? new KeyView { Image = image }
        : new KeyView { Background = Rgb.Hex(0x111318), Accent = Rgb.Hex(0xFF6330), Icon = KeyIcon.Spark };

    public static KeyView TestPattern(int index) => new()
    {
        Background = Rgb.Hex(0x1E3A8A), Foreground = Rgb.Hex(0xFFFFFF), Muted = Rgb.Hex(0xBFDBFE),
        TestPatternIndex = index,
    };

    public static KeyView Message(string title, string main, string? sub = null) => new()
    {
        Background = C.GaugeBg, Foreground = C.GaugeFg, Muted = C.GaugeMuted, Title = title, Main = main, Sub = sub,
    };
}
