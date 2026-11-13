using System.Text.Json;
using ClaudeDeck.Core.Rendering;
using ClaudeDeck.Core.Usage;

namespace ClaudeDeck.Tests;

public class UsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_status_line_rate_limits_epoch_seconds()
    {
        var p = J.E(new
        {
            rate_limits = new
            {
                five_hour = new { used_percentage = 23.5, resets_at = 1738425600 },
                seven_day = new { used_percentage = 41.2, resets_at = 1738857600 },
            },
        });
        var (five, week) = PlanUsageStore.ParseStatusLine(p, Now);
        Assert.Equal(23.5, five!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1738425600), five.ResetsAt);
        Assert.Equal(41.2, week!.UsedPercent);
        Assert.Equal("statusline", week.Source);
    }

    [Fact]
    public void Parses_iso_reset_and_missing_windows()
    {
        var p = J.E(new { rate_limits = new { five_hour = new { used_percentage = 5, resets_at = "2026-11-08T15:00:00Z" } } });
        var (five, week) = PlanUsageStore.ParseStatusLine(p, Now);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero), five!.ResetsAt);
        Assert.Null(week);
        Assert.Equal((null, null), PlanUsageStore.ParseStatusLine(J.E(new { model = "x" }), Now));
    }

    [Fact]
    public void Parses_oauth_utilization()
    {
        var p = J.E(new { five_hour = new { utilization = 12.0, resets_at = "2026-11-08T12:00:00+00:00" }, seven_day = new { utilization = 80 } });
        var (five, week) = PlanUsageStore.ParseOAuth(p, Now);
        Assert.Equal(12, five!.UsedPercent);
        Assert.Equal(80, week!.UsedPercent);
        Assert.Equal("oauth", five.Source);
    }

    [Fact]
    public void Store_keeps_newest_and_persists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cd-plan-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PlanUsageStore(path);
            store.Update(new RateWindow(50, null, Now, "statusline"), null);
            store.Update(new RateWindow(10, null, Now.AddMinutes(-5), "oauth"), null); // plus ancien : ignoré
            Assert.Equal(50, store.Snapshot().FiveHour!.UsedPercent);

            var reloaded = new PlanUsageStore(path);
            Assert.Equal(50, reloaded.Snapshot().FiveHour!.UsedPercent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Window_reset_detection()
    {
        var w = new RateWindow(70, Now.AddMinutes(10), Now, "statusline");
        Assert.False(w.HasReset(Now));
        Assert.True(w.HasReset(Now.AddMinutes(10)));
    }

    [Fact]
    public void Cost_report_amounts_are_cents()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[
              {"starting_at":"2026-11-07T00:00:00Z","ending_at":"2026-11-08T00:00:00Z","results":[
                {"amount":"123.45","currency":"USD"},{"amount":"76.55","currency":"USD"}]},
              {"starting_at":"2026-11-08T00:00:00Z","ending_at":"2026-11-09T00:00:00Z","results":[]}],
             "has_more":true,"next_page":"page_abc"}
            """);
        var (items, hasMore, next) = AdminCostClient.ParsePage(doc.RootElement);
        Assert.Equal(2, items.Count);
        Assert.Equal(2.00m, items[0].Usd);
        Assert.Equal(0m, items[1].Usd);
        Assert.True(hasMore);
        Assert.Equal("page_abc", next);
    }

    [Fact]
    public void Balance_estimate_subtracts_spend_since_baseline()
    {
        var setAt = new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.Zero);
        var daily = new List<DailyCost>
        {
            new(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), 9m),  // avant la saisie : ignoré
            new(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), 5m),  // dont 3 $ déjà dépensés à la saisie
            new(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero), 4m),
        };
        Assert.Equal(100m - (9m - 3m), BalanceEstimator.Estimate(100m, setAt, 3m, daily));
        Assert.Null(BalanceEstimator.Estimate(null, setAt, 0, daily));
    }
}

public class AdminKeyTests
{
    [Theory]
    [InlineData("  sk-ant-admin01-abc\r\n", "sk-ant-admin01-abc")]
    [InlineData("\"sk-ant-admin01-abc\"", "sk-ant-admin01-abc")]
    [InlineData("﻿sk-ant-admin01-​abc", "sk-ant-admin01-abc")]
    [InlineData("x-api-key: sk-ant-admin01-abc", "sk-ant-admin01-abc")]
    [InlineData("Bearer sk-ant-admin01-abc", "sk-ant-admin01-abc")]
    [InlineData("   ", null)]
    public void Sanitize_key(string raw, string? expected) => Assert.Equal(expected, AdminCostClient.SanitizeKey(raw));

    [Fact]
    public void Admin_key_prefix()
    {
        Assert.True(AdminCostClient.LooksLikeAdminKey("sk-ant-admin01-xyz"));
        Assert.False(AdminCostClient.LooksLikeAdminKey("sk-ant-api03-xyz"));
    }

    [Fact]
    public void Error_label_uses_status_not_body_text()
    {
        // Un identifiant de requête contenant « 401 » ne doit pas faire croire à une clé refusée.
        var ex = AdminApiException.FromResponse(400,
            """{"type":"error","error":{"type":"invalid_request_error","message":"bad param"},"request_id":"req_0401401"}""");
        Assert.Equal(400, ex.Status);
        Assert.Equal("invalid_request_error", ex.ErrorType);
        Assert.Equal("bad param", ex.ApiMessage);
        Assert.Equal("requête refusée", ex.ShortLabel);
    }

    [Fact]
    public void Explain_401_with_regular_api_key_suggests_admin_key()
    {
        var ex = AdminApiException.FromResponse(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        Assert.Equal("clé refusée", ex.ShortLabel);
        var msg = UsageService.Explain(ex, "sk-ant-api03-xyz");
        Assert.Contains("invalid x-api-key", msg);
        Assert.Contains("clé personnelle", msg);
    }
}

public class UsageCreditsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_extra_usage_in_minor_units()
    {
        var c = PlanUsageStore.ParseOAuthCredits(J.E(new
        {
            five_hour = new { utilization = 100.0 },
            extra_usage = new { is_enabled = true, monthly_limit = 5000, used_credits = 1240.0, utilization = 24.8, currency = "EUR", decimal_places = 2 },
        }), Now)!;
        Assert.True(c.Enabled);
        Assert.Equal(50m, c.MonthlyLimit);
        Assert.Equal(12.40m, c.Used);
        Assert.Equal(37.60m, c.Remaining);
        Assert.Equal("EUR", c.Currency);
        Assert.Equal(24.8, c.UsedPercent!.Value, 3);

        var off = PlanUsageStore.ParseOAuthCredits(J.E(new { extra_usage = new { is_enabled = false, monthly_limit = (int?)null, used_credits = (int?)null } }), Now)!;
        Assert.False(off.Enabled);
        Assert.Null(off.Remaining);
        Assert.Null(PlanUsageStore.ParseOAuthCredits(J.E(new { five_hour = new { utilization = 3 } }), Now));
    }

    [Fact]
    public void Session_tile_shows_credit_balance_only_when_exhausted_and_enabled()
    {
        var b = new KeyViewBuilder(TimeZoneInfo.Utc);
        var full = new RateWindow(100, Now.AddHours(2), Now, "statusline");
        var credits = new UsageCredits(true, 50m, 12.40m, "EUR", Now);
        string N(string? s) => (s ?? "").Replace(' ', ' ').Replace(' ', ' ');

        Assert.Equal("37,60 €", N(b.SessionWindow(full, credits, Now).Main));
        Assert.Equal("100 %", N(b.SessionWindow(full, credits with { Enabled = false }, Now).Main)); // crédits désactivés
        Assert.Equal("100 %", N(b.SessionWindow(full, null, Now).Main));                            // inconnu
        Assert.Equal("62 %", N(b.SessionWindow(full with { UsedPercent = 62 }, credits, Now).Main)); // forfait non épuisé
        Assert.Equal("SESSION", b.SessionWindow(full, credits, Now).Title);

        // Solde prépayé connu : c'est lui qui est affiché.
        Assert.Equal("23,50 €", N(b.SessionWindow(full, credits with { Balance = 23.50m }, Now).Main));

        // Sans plafond, recharge automatique activée : avertissement à la place de la jauge.
        var reload = b.SessionWindow(full, credits with { MonthlyLimit = null, Balance = 23.50m, AutoReload = true }, Now);
        Assert.Equal("23,50 €", N(reload.Main));
        Assert.Equal("RECHARGE AUTO", reload.BarText);
        Assert.StartsWith("→ ", reload.Sub);

        // Sans plafond ni recharge automatique : jauge pleine, rouge.
        var noReload = b.SessionWindow(full, credits with { MonthlyLimit = null, Balance = 23.50m, AutoReload = false }, Now);
        Assert.Null(noReload.BarText);
        Assert.Equal(1.0, noReload.Bar);
        Assert.Equal(Rgb.Hex(0xEF4444), noReload.BarColor);
        Assert.Null(b.SessionWindow(full, credits with { MonthlyLimit = null }, Now).BarText); // recharge inconnue

        using var r = new KeyRenderer();
        Assert.Equal(80 * 80 * 4, r.Render(reload).Rgba.Length);
    }

    [Fact]
    public void Free_reset_seen_by_oauth_is_not_undone_by_stale_status_line()
    {
        var store = new PlanUsageStore();
        var oldReset = Now.AddHours(2);
        store.Update(new RateWindow(100, oldReset, Now, "statusline"), null);
        // « Réinitialiser gratuitement » : OAuth voit la nouvelle fenêtre à 0 %.
        store.Update(new RateWindow(0, Now.AddHours(5), Now.AddMinutes(1), "oauth"), null);
        Assert.Equal(0, store.Snapshot().FiveHour!.UsedPercent);
        // La status line renvoie encore l'ancienne fenêtre : ignorée.
        store.Update(new RateWindow(100, oldReset, Now.AddMinutes(2), "statusline"), null);
        Assert.Equal(0, store.Snapshot().FiveHour!.UsedPercent);
        // Réponse suivante de Claude : nouvelle fenêtre, acceptée.
        store.Update(new RateWindow(3, Now.AddHours(5), Now.AddMinutes(3), "statusline"), null);
        Assert.Equal(3, store.Snapshot().FiveHour!.UsedPercent);
    }

    [Fact]
    public void Parses_prepaid_credits_response()
    {
        var (balance, currency, reload) = PlanUsageStore.ParsePrepaid(J.E(new
        {
            amount = 2350, currency = "eur", auto_reload_settings = new { enabled = true, threshold_cents = 500 }, expiry_policy_months = 12,
        }));
        Assert.Equal(23.50m, balance);
        Assert.Equal("EUR", currency);
        Assert.True(reload);
        var empty = PlanUsageStore.ParsePrepaid(J.E(new { currency = "USD" }));
        Assert.Null(empty.Balance);
        Assert.Null(empty.AutoReload);
    }

    [Fact]
    public void Config_v2_enables_oauth_source()
    {
        var cfg = new ClaudeDeck.Core.Config.DeckConfig { SchemaVersion = 2 };
        cfg.PlanUsage.OAuthFallback = false;
        Assert.True(ClaudeDeck.Core.Config.ConfigStore.Migrate(cfg));
        Assert.True(cfg.PlanUsage.OAuthFallback);
    }
}
