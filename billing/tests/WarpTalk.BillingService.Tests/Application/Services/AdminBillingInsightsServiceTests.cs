using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Testcontainers.PostgreSql;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.BillingService.Tests.Integration;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// Every billing Insights metric on seeded rows in real PostgreSQL (Testcontainers — needs Docker).
/// The repository aggregates group, join and correlate; an in-memory provider would evaluate LINQ
/// that PostgreSQL cannot translate, which is exactly how a positional-record projection shipped a
/// 500 before.
///
/// "Now" is 17 Sep 2026 08:00Z. The seed, and what each row is for:
///
/// Payments (FX 25,000 VND/USD)
///   P9   paid  300,000 VND  20 Aug           S1  previous-month revenue; S1's first paid payment
///   P1   paid 1,000,000 VND  5 Sep 10:00 cs_a S1  counted
///   P2   paid 1,000,000 VND  5 Sep 10:00 in_a S1  same checkout's invoice → not counted
///   P3   paid   500,000 VND 10 Sep       in_b S1  renewal invoice, no twin → counted
///   P4   paid 2,000,000 VND 12 Sep internal S2  manual mark-paid → counted
///   P5   paid       20 USD 16 Sep 12:00 cs_c S8  converted → 500,000 (yesterday)
///   P13  paid   150,000 VND 17 Sep 07:00      S1  today
///   P6   failed              6 Sep            S1
///   P7   pending 990,000 VND                  S8  outstanding invoice I1, due 5 Sep → past due
///   P14  pending 1,000,000 VND                S2  outstanding invoice I2, due 30 Sep
///   P8   refunded            8 Sep            S1  excluded
///   P10  subscription_updated 9 Sep           S1  excluded
///   P11a/b/c paid 100,000 VND 5 Jul           S4/S5/S6
///   → Sep revenue 4,150,000 over 5 payments; Aug 300,000 over 1.
///
/// Consume transactions (Sep)
///   tx1 W1 −100  after 900     usage 200 s, TRANSLATION card with cost 0.001 USD/s → 0.2 USD
///   tx2 W1 −300  after −100    usage 100 s, dubbing card with NO provider cost     overage 100
///   tx3 W1 −50   after −150    no charge type, usage type STT, no card             overage 50
///   tx5 W2 −1000 after 0       usage 1000 s, TRANSLATION card with cost → 1 USD
///   tx6 W2 −60000 after −60000 TRANSLATION, no usage row (17 Sep 01:00)             overage 60,000
///   plus a +500 top-up that is not consumption
///   → 61,450 credits, 60,150 overage, cost 1.2 USD = 30,000 VND on 1,100 covered credits.
///
/// Subscriptions
///   S1 W1 monthly, first paid 20 Aug, renews 20 Sep     S2 W2 contract 2,000,000/month, created 3 Sep
///   S3 W3 trial created 4 Sep, ends 18 Sep              S4 W4 paid Jul, cancelled, period ended 10 Sep
///   S5 W5 paid Jul, cancel-at-period-end 25 Sep         S6 W6 paid Jul, cancelled 12 Sep, replaced by S7
///   S7 W6 contract 12,000,000 on a yearly plan, created 12 Sep 00:10
///   S8 W8 20 USD monthly, suspended for an overdue invoice
/// </summary>
public sealed class AdminBillingInsightsServiceTests : IAsyncLifetime
{
    private static DateTime Utc(int m, int d, int h = 0, int min = 0) => new(2026, m, d, h, min, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = Utc(9, 17, 8);

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    private readonly Guid _monthly = Guid.NewGuid(), _yearly = Guid.NewGuid(), _usdPlan = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _w1 = Guid.NewGuid(), _w2 = Guid.NewGuid(), _w3 = Guid.NewGuid(), _w4 = Guid.NewGuid(),
        _w5 = Guid.NewGuid(), _w6 = Guid.NewGuid(), _w8 = Guid.NewGuid();
    private readonly Guid _s1 = Guid.NewGuid(), _s2 = Guid.NewGuid(), _s3 = Guid.NewGuid(), _s4 = Guid.NewGuid(),
        _s5 = Guid.NewGuid(), _s6 = Guid.NewGuid(), _s7 = Guid.NewGuid(), _s8 = Guid.NewGuid();
    private readonly Guid _p7 = Guid.NewGuid();

    private BillingDbContext _context = null!;
    private AdminBillingInsightsService _service = null!;

    public async Task InitializeAsync()
    {
        if (!DockerAvailable()) return;

        await _database.StartAsync();
        _context = new BillingDbContext(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(_database.GetConnectionString()).Options);

        // Same uuidv7() shim as AdminWorkspaceAnalyticsServiceTests: postgres:16 has no builtin.
        await _context.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION uuidv7() RETURNS uuid AS $$
            BEGIN RETURN gen_random_uuid(); END;
            $$ LANGUAGE plpgsql;
            """);
        await _context.Database.EnsureCreatedAsync();

        var workspaces = new Mock<IWorkspaceClient>();
        workspaces
            .Setup(w => w.GetWorkspaceNamesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Guid> ids, CancellationToken _) =>
                Result.Success(ids.ToDictionary(id => id, id => id == _w1 ? "Hanoi Law Firm" : $"ws-{id.ToString()[..4]}")));

        _service = new AdminBillingInsightsService(
            new UnitOfWork(_context),
            new UsageRateCardRepository(_context),
            workspaces.Object,
            NullLogger<AdminBillingInsightsService>.Instance,
            new FixedTime(Now));

        await SeedAsync();
        _context.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    // ── Period insights ─────────────────────────────────────────────────────

    // The seeded September is the UTC one; the Vietnam calendar (the default tz) has its own tests.
    private async Task<AdminBillingInsightsDto> SeptemberAsync(string compare = "previousMonth")
    {
        var result = await _service.GetInsightsAsync(new AdminInsightsQuery
        {
            From = Utc(9, 1), To = Utc(10, 1), Compare = compare, Tz = "UTC",
        });
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    private static AdminInsightMetric M(AdminBillingInsightsDto dto, string id) => dto.Metrics.Single(m => m.Id == id);

    [DockerFact]
    public async Task MetricsAreReturnedInContractOrder()
    {
        (await SeptemberAsync()).Metrics.Select(m => m.Id).Should().Equal(
            "revenue", "payments", "failedPayments", "newSubscriptions", "cancelledSubscriptions",
            "creditsConsumed", "overageCredits", "aiProviderCost", "grossMargin", "revenuePerPayment",
            "activeWorkspaces");
    }

    /// <summary>
    /// WT-692: a workspace is active in a period when it consumed credits in it. W1 and W2 did in
    /// September; the top-up is not use, and nothing was consumed in August.
    /// </summary>
    [DockerFact]
    public async Task ActiveWorkspacesAreTheWorkspacesThatConsumed()
    {
        var dto = await SeptemberAsync();

        var active = M(dto, "activeWorkspaces");
        active.Value.Should().Be(2);
        active.Previous.Should().Be(0);
        active.Unit.Should().Be("count");
        active.HigherIsBetter.Should().BeTrue();

        dto.ActiveWorkspacesByMonth!.Select(m => m.Month).Should().Equal(
            "2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09");
        dto.ActiveWorkspacesByMonth!.Select(m => m.ActiveWorkspaces).Should().Equal(0, 0, 0, 0, 0, 2);
    }

    /// <summary>
    /// Profit and loss over the same seeded September: the same revenue and AI cost as the Insights cards
    /// (no fx_rates rows, so the configured 25,000 converts), from the half-hour slot queries.
    /// </summary>
    [DockerFact]
    public async Task ProfitAndLoss_AgreesWithTheInsightsFigures()
    {
        var result = await _service.GetProfitAndLossAsync(new AdminInsightsQuery
        {
            From = Utc(9, 1), To = Utc(10, 1), Compare = "previousMonth", Tz = "UTC",
        });
        result.IsSuccess.Should().BeTrue(result.Error);
        var pnl = result.Value!;
        AdminInsightMetric P(string id) => pnl.Metrics.Single(m => m.Id == id);

        P("revenue").Value.Should().Be(4_150_000m);
        P("aiProviderCost").Value.Should().Be(30_000m);
        P("grossMargin").Value.Should().Be(4_120_000m);
        P("creditsConsumed").Value.Should().Be(61_450m);
        P("activeWorkspaces").Value.Should().Be(3, "W1 and W2 consumed, W8 paid");
        P("arpa").Value.Should().Be(1_383_333m);
        pnl.Days.Should().HaveCount(30);
        pnl.Days.Sum(d => d.Credits).Should().Be(61_450);
        pnl.Months.Select(m => m.Key).Should().EndWith("2026-09");
        pnl.TopWorkspaces.First().WorkspaceId.Should().Be(_w2);
        pnl.TopWorkspaces.First().Days.Sum().Should().Be(61_000);
        pnl.Plans.Should().NotBeEmpty();
        pnl.Providers.Should().NotBeEmpty();
    }

    [DockerFact]
    public async Task EchoesRangeAndPreviousMonthRange()
    {
        var dto = await SeptemberAsync();

        dto.Range.Should().Be(new AdminInsightRange(Utc(9, 1), Utc(10, 1)));
        dto.PreviousRange.Should().Be(new AdminInsightRange(Utc(8, 1), Utc(9, 1)));
        dto.GeneratedAt.Should().Be(Now);
    }

    [DockerFact]
    public async Task Revenue_CountsEachPaidChargeOnce()
    {
        var dto = await SeptemberAsync();

        var revenue = M(dto, "revenue");
        revenue.Value.Should().Be(4_150_000m);
        revenue.Previous.Should().Be(300_000m);
        revenue.Unit.Should().Be("money");
        revenue.Note.Should().Contain("includes 20.00 USD converted at 25,000 VND/USD")
            .And.Contain("1 Stripe subscription invoice(s) counted once");

        M(dto, "payments").Value.Should().Be(5);
        M(dto, "payments").Previous.Should().Be(1);
        M(dto, "failedPayments").Should().BeEquivalentTo(new { Value = 1m, Previous = 0m, HigherIsBetter = false });
        M(dto, "revenuePerPayment").Value.Should().Be(830_000m);
        M(dto, "revenuePerPayment").Previous.Should().Be(300_000m);
    }

    [DockerFact]
    public async Task SubscriptionFlow_UsesFirstPaymentAndPaidPeriodEnd()
    {
        var dto = await SeptemberAsync();

        // New: S2 and S7 (contracts) and S8 (first paid payment 16 Sep). Not S1 (first paid in Aug), not S3 (trial).
        M(dto, "newSubscriptions").Value.Should().Be(3);
        M(dto, "newSubscriptions").Previous.Should().Be(1);
        M(dto, "newSubscriptions").Note.Should().Contain("1 trial(s) started");

        // Cancelled: S4 only. S5 ends in the future; S6 was replaced by S7 within the hour.
        M(dto, "cancelledSubscriptions").Value.Should().Be(1);
        M(dto, "cancelledSubscriptions").Previous.Should().Be(0);
    }

    [DockerFact]
    public async Task Credits_OverageAndProviderCost()
    {
        var dto = await SeptemberAsync();

        M(dto, "creditsConsumed").Should().BeEquivalentTo(new { Value = 61_450m, Previous = 0m, Unit = "credits" });
        M(dto, "overageCredits").Value.Should().Be(60_150m);

        var cost = M(dto, "aiProviderCost");
        cost.Value.Should().Be(30_000m);
        cost.Previous.Should().Be(0m);
        cost.HigherIsBetter.Should().BeFalse();
        // The partial figure names what it leaves out: tx6 (no usage row) and the costless dubbing
        // card are TRANSLATION / AUDIO_DUBBING_STANDARD; tx3 has no charge type, so its usage type.
        cost.Note.Should().Be(
            "covers 1.8% of consumed credits (2 of 5 transactions have a provider cost); "
            + "no provider cost for TRANSLATION (97.6%), AUDIO_DUBBING_STANDARD (0.5%), STT (0.1%); "
            + "USD converted at 25,000 VND/USD");

        M(dto, "grossMargin").Value.Should().Be(4_120_000m);
        M(dto, "grossMargin").Note.Should().Be(
            "AI cost covers only 1.8% of consumed credits (no provider cost for TRANSLATION (97.6%), "
            + "AUDIO_DUBBING_STANDARD (0.5%), STT (0.1%)), so this margin is overstated");
    }

    [DockerFact]
    public async Task Series_And_Breakdowns()
    {
        var dto = await SeptemberAsync();

        dto.RevenueByDay.Should().HaveCount(30);
        dto.RevenueByDay.Single(d => d.Date == "2026-09-05").Revenue.Should().Be(1_000_000m);
        dto.RevenueByDay.Single(d => d.Date == "2026-09-16").Revenue.Should().Be(500_000m);
        dto.RevenueByDay.Sum(d => d.Revenue).Should().Be(M(dto, "revenue").Value);

        dto.RevenueByMonth.Should().Equal(
            new AdminRevenueByMonthDto("2026-04", 0m),
            new AdminRevenueByMonthDto("2026-05", 0m),
            new AdminRevenueByMonthDto("2026-06", 0m),
            new AdminRevenueByMonthDto("2026-07", 300_000m),
            new AdminRevenueByMonthDto("2026-08", 300_000m),
            new AdminRevenueByMonthDto("2026-09", 4_150_000m));

        dto.CreditsByService.Should().Equal(
            new AdminCreditsByServiceDto("TRANSLATION", 61_100),
            new AdminCreditsByServiceDto("AUDIO_DUBBING_STANDARD", 300),
            new AdminCreditsByServiceDto("STT", 50));

        dto.TopWorkspaces.Select(t => (t.WorkspaceId, t.Credits)).Should().Equal((_w2, 61_000L), (_w1, 450L));
        dto.TopWorkspaces[1].WorkspaceName.Should().Be("Hanoi Law Firm");
    }

    [DockerFact]
    public async Task ComparePrevious_UsesTheSameLengthBefore()
    {
        var result = await _service.GetInsightsAsync(new AdminInsightsQuery { From = Utc(9, 5), To = Utc(9, 6), Tz = "UTC" });

        result.Value!.PreviousRange.Should().Be(new AdminInsightRange(Utc(9, 4), Utc(9, 5)));
        M(result.Value, "revenue").Value.Should().Be(1_000_000m);
        M(result.Value, "revenue").Previous.Should().Be(0m);
        M(result.Value, "revenuePerPayment").Previous.Should().BeNull();
    }

    [DockerFact]
    public async Task MissingFxRate_NullsCostAndExcludesUsd()
    {
        await _context.BillingPricingConfigs.Where(c => c.Key == "fx_rate_usd_vnd").ExecuteDeleteAsync();

        var dto = await SeptemberAsync();

        M(dto, "revenue").Value.Should().Be(3_650_000m);
        M(dto, "revenue").Note.Should().Contain("excludes 1 USD rows (no fx_rate_usd_vnd configured)");
        M(dto, "aiProviderCost").Value.Should().BeNull();
        M(dto, "grossMargin").Value.Should().BeNull();
        M(dto, "grossMargin").Note.Should().Be("AI provider cost is unavailable");
    }

    [DockerFact]
    public async Task InvalidRange_IsAValidationError()
    {
        (await _service.GetInsightsAsync(new AdminInsightsQuery { From = Utc(9, 2), To = Utc(9, 1) }))
            .ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    // ── Snapshot ────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Snapshot_RightNow()
    {
        var result = await _service.GetSnapshotAsync("UTC");
        result.IsSuccess.Should().BeTrue(result.Error);
        var s = result.Value!;

        s.GeneratedAt.Should().Be(Now);
        s.RevenueToday.Should().Be(150_000m);
        s.RevenueTodayNote.Should().BeNull();
        s.RevenueYesterday.Should().Be(500_000m);
        s.RevenueYesterdayNote.Should().Contain("includes 20.00 USD converted");

        // S1 500,000 + S2 2,000,000 + S7 12,000,000/12 + S8 20 USD × 25,000.
        s.Mrr.Should().Be(4_000_000m);
        s.MrrNote.Should().Contain("20.00 USD");
        s.ActiveSubscriptions.Should().Be(4);
        s.ActiveByCycle.Should().Be(new AdminActiveByCycleDto(3, 1, 0));

        // Active at 1 Sep: S1, S4, S5, S6. Cancelled since: S4.
        s.ChurnRateMonth.Should().Be(new AdminChurnRateMonthDto(1, 4, 25.00m));

        s.Trials.Should().Be(1);
        s.TrialsEndingThisWeek.Should().Be(1);
        s.PastDue.Should().Be(1);
        s.Suspended.Should().Be(1);
        s.ActiveWorkspaces.Should().Be(5);
        s.PlatformCreditBalance.Should().Be(1_000 + 2_000 + 3_000 + 7_000 + 8_000);

        s.OutstandingInvoices.Should().Be(new AdminOutstandingInvoicesDto(2, 1_990_000m, null, 1, 12, $"ws-{_w8.ToString()[..4]}"));
        s.OpenSalesLeads.Should().Be(2);

        s.SubscriptionsByPlan.Should().BeEquivalentTo(new[]
        {
            new AdminSubscriptionsByPlanDto("team", "Team", 2, 1, 0),
            new AdminSubscriptionsByPlanDto("business-yearly", "Business Yearly", 1, 0, 0),
            new AdminSubscriptionsByPlanDto("starter-usd", "Starter USD", 0, 0, 1),
        });
    }

    [DockerFact]
    public async Task Snapshot_Lists()
    {
        var s = (await _service.GetSnapshotAsync("UTC")).Value!;

        s.RecentPayments.Should().HaveCount(8);
        s.RecentPayments.Select(p => p.At).Should().BeInDescendingOrder();
        s.RecentPayments.Select(p => p.Status).Should().NotContain(["pending", "subscription_updated"]);
        s.RecentPayments.Count(p => p.Amount == 1_000_000m && p.Status == "paid").Should().Be(1, "the in_ twin is hidden");
        s.RecentPayments[0].Should().BeEquivalentTo(new
        {
            WorkspaceId = (Guid?)_w1, WorkspaceName = "Hanoi Law Firm", Amount = 150_000m, Currency = "VND", Method = "Stripe card",
        });
        s.RecentPayments.Single(p => p.Amount == 2_000_000m).Method.Should().Be("Invoice");

        s.EndingSoon.Select(e => (e.WorkspaceId, e.CancelAtPeriodEnd)).Should().Equal((_w1, false), (_w5, true), (_w8, false));

        s.HighUsageAlerts.Should().Equal(new AdminHighUsageAlertDto(_w2, $"ws-{_w2.ToString()[..4]}", 60_000));
    }

    // ── Time zone ───────────────────────────────────────────────────────────

    private async Task AddPaidAsync(decimal total, string currency, DateTime at, string providerTxId)
    {
        _context.Payments.Add(NewPayment(_s1, total, currency, "paid", at, providerTxId));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [DockerFact]
    public async Task Snapshot_TodayIsTheVietnamDayByDefault()
    {
        // 18:00Z on 16 Sep is 01:00 on 17 Sep in Vietnam. "Now" is 17 Sep 15:00 there.
        await AddPaidAsync(70_000m, "VND", Utc(9, 16, 18), "cs_vn_night");

        var vietnam = (await _service.GetSnapshotAsync(null)).Value!;
        vietnam.RevenueToday.Should().Be(150_000m + 70_000m);
        vietnam.RevenueYesterday.Should().Be(500_000m);

        var utc = (await _service.GetSnapshotAsync("UTC")).Value!;
        utc.RevenueToday.Should().Be(150_000m);
        utc.RevenueYesterday.Should().Be(500_000m + 70_000m);
    }

    [DockerFact]
    public async Task Snapshot_RevenueToday_NotesAnUnconvertibleCurrencyInsteadOfDroppingIt()
    {
        await AddPaidAsync(9m, "EUR", Utc(9, 17, 6), "cs_eur_today");

        var s = (await _service.GetSnapshotAsync("Asia/Ho_Chi_Minh")).Value!;

        s.RevenueToday.Should().Be(150_000m);
        s.RevenueTodayNote.Should().Be("excludes 1 EUR rows");
    }

    [DockerFact]
    public async Task Snapshot_UnknownTz_IsAValidationError()
    {
        (await _service.GetSnapshotAsync("Mars/Olympus_Mons")).ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [DockerFact]
    public async Task Insights_VietnamSeptember_BucketsDaysAndComparesWithVietnamAugust()
    {
        // 18:00Z on 31 Aug is 01:00 on 1 Sep in Vietnam: September revenue there, August in UTC.
        await AddPaidAsync(70_000m, "VND", Utc(8, 31, 18), "cs_vn_first");

        var result = await _service.GetInsightsAsync(new AdminInsightsQuery
        {
            From = Utc(8, 31, 17), To = Utc(9, 30, 17), Compare = "previousMonth",
        });
        result.IsSuccess.Should().BeTrue(result.Error);
        var dto = result.Value!;

        dto.PreviousRange.Should().Be(new AdminInsightRange(Utc(7, 31, 17), Utc(8, 31, 17)));
        dto.RevenueByDay.Should().HaveCount(30);
        dto.RevenueByDay[0].Should().Be(new AdminRevenueByDayDto("2026-09-01", 70_000m));
        dto.RevenueByDay[^1].Date.Should().Be("2026-09-30");
        M(dto, "revenue").Value.Should().Be(4_150_000m + 70_000m);
        M(dto, "revenue").Previous.Should().Be(300_000m);
        dto.RevenueByMonth.Select(m => m.Month).Should().Equal("2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09");
        dto.RevenueByMonth.Single(m => m.Month == "2026-09").Revenue.Should().Be(4_150_000m + 70_000m);
        dto.RevenueByMonth.Single(m => m.Month == "2026-08").Revenue.Should().Be(300_000m);
        dto.RevenueByDayNote.Should().BeNull();
    }

    // ── Seed ────────────────────────────────────────────────────────────────

    private async Task SeedAsync()
    {
        _context.Plans.AddRange(
            NewPlan(_monthly, "Team", "team", "monthly", 500_000m, "VND"),
            NewPlan(_yearly, "Business Yearly", "business-yearly", "yearly", 6_000_000m, "VND"),
            NewPlan(_usdPlan, "Starter USD", "starter-usd", "monthly", 20m, "USD"));

        _context.Subscriptions.AddRange(
            NewSub(_s1, _w1, _monthly, created: Utc(8, 1), periodEnd: Utc(9, 20), credits: 1_000),
            NewSub(_s2, _w2, _monthly, created: Utc(9, 3), periodEnd: Utc(10, 3), credits: 2_000, contract: 2_000_000m),
            NewSub(_s3, _w3, _monthly, created: Utc(9, 4), periodEnd: Utc(9, 18), credits: 3_000, trialEndsAt: Utc(9, 18), autoRenew: false),
            NewSub(_s4, _w4, _monthly, created: Utc(7, 1), periodEnd: Utc(9, 10), status: "cancelled", isActive: false, autoRenew: false),
            NewSub(_s5, _w5, _monthly, created: Utc(7, 1), periodEnd: Utc(9, 25), status: "cancelled", autoRenew: false),
            NewSub(_s6, _w6, _monthly, created: Utc(7, 1), periodEnd: Utc(10, 1), status: "cancelled", isActive: false, cancelledAt: Utc(9, 12)),
            NewSub(_s7, _w6, _yearly, created: Utc(9, 12, 0, 10), periodEnd: Utc(9, 12).AddYears(1), credits: 7_000, contract: 12_000_000m),
            NewSub(_s8, _w8, _usdPlan, created: Utc(6, 1), periodEnd: Utc(9, 30), credits: 8_000,
                serviceState: "suspended", suspendedReason: "invoice_overdue"));

        var p4 = Guid.NewGuid();
        var p14 = Guid.NewGuid();
        _context.Payments.AddRange(
            NewPayment(_s1, 300_000m, "VND", "paid", Utc(8, 20), "cs_prev"),
            NewPayment(_s1, 1_000_000m, "vnd", "paid", Utc(9, 5, 10), "cs_a"),
            NewPayment(_s1, 1_000_000m, "vnd", "paid", Utc(9, 5, 10).AddSeconds(30), "in_a"),
            NewPayment(_s1, 500_000m, "vnd", "paid", Utc(9, 10), "in_b"),
            NewPayment(_s2, 2_000_000m, "VND", "paid", Utc(9, 12), "cycle-s2", provider: "internal_invoice", method: "invoice", id: p4),
            NewPayment(_s8, 20m, "usd", "paid", Utc(9, 16, 12), "cs_c"),
            NewPayment(_s1, 150_000m, "vnd", "paid", Utc(9, 17, 7), "cs_today"),
            NewPayment(_s1, 990_000m, "vnd", "failed", Utc(9, 6), "cs_failed"),
            NewPayment(_s8, 990_000m, "VND", "pending", Utc(8, 5), "cycle-s8", provider: "internal_invoice", method: "invoice", id: _p7),
            NewPayment(_s2, 1_000_000m, "VND", "pending", Utc(9, 2), "cycle-s2-b", provider: "internal_invoice", method: "invoice", id: p14),
            NewPayment(_s1, 1_000_000m, "vnd", "refunded", Utc(9, 8), "cs_refunded"),
            NewPayment(_s1, 0m, "vnd", "subscription_updated", Utc(9, 9), "sub_1"),
            NewPayment(_s4, 100_000m, "VND", "paid", Utc(7, 5), "cs_s4"),
            NewPayment(_s5, 100_000m, "VND", "paid", Utc(7, 5), "cs_s5"),
            NewPayment(_s6, 100_000m, "VND", "paid", Utc(7, 5), "cs_s6"));

        _context.Invoices.AddRange(
            NewInvoice(_p7, 990_000m, "open", dueAt: Utc(9, 5, 6)),
            NewInvoice(p14, 1_000_000m, "open", dueAt: Utc(9, 30)),
            NewInvoice(p4, 2_000_000m, "paid", dueAt: Utc(9, 20)));

        var costCard = new UsageRateCard
        {
            Id = Guid.NewGuid(), ChargeType = "TRANSLATION", Unit = "second", Currency = "CRD", UnitPrice = 0.5m,
            ProviderUnitCost = 0.001m, EffectiveFrom = Utc(1, 1), IsActive = true,
        };
        var noCostCard = new UsageRateCard
        {
            Id = Guid.NewGuid(), ChargeType = "AUDIO_DUBBING_STANDARD", Unit = "second", Currency = "CRD", UnitPrice = 3m,
            EffectiveFrom = Utc(1, 1), IsActive = true,
        };
        _context.UsageRateCards.AddRange(costCard, noCostCard);
        _context.BillingPricingConfigs.Add(new BillingPricingConfig { Key = "fx_rate_usd_vnd", Value = 25_000m, UpdatedAt = Utc(1, 1) });

        var u1 = NewUsage(_s1, _w1, "TRANSLATION", "second", 200m, 100, Utc(9, 2));
        var u2 = NewUsage(_s1, _w1, "AUDIO_DUBBING_STANDARD", "second", 100m, 300, Utc(9, 2, 1));
        var u3 = NewUsage(_s1, _w1, "STT", "second", 50m, 50, Utc(9, 3));
        var u5 = NewUsage(_s2, _w2, "TRANSLATION", "second", 1000m, 1000, Utc(9, 4));
        _context.UsageRecords.AddRange(u1, u2, u3, u5);

        _context.CreditTransactions.AddRange(
            NewTx(_s1, _w1, -100, "consume", 900, Utc(9, 2), "TRANSLATION", costCard.Id, u1.Id),
            NewTx(_s1, _w1, -300, "consume", -100, Utc(9, 2, 1), "AUDIO_DUBBING_STANDARD", noCostCard.Id, u2.Id),
            NewTx(_s1, _w1, -50, "consume", -150, Utc(9, 3), null, null, u3.Id),
            NewTx(_s2, _w2, -1000, "consume", 0, Utc(9, 4), "TRANSLATION", costCard.Id, u5.Id),
            NewTx(_s2, _w2, -60_000, "consume", -60_000, Utc(9, 17, 1), "TRANSLATION", null, null),
            NewTx(_s1, _w1, 500, "top_up", 350, Utc(9, 5), null, null, null));

        _context.SalesInquiries.AddRange(NewLead("new"), NewLead("reviewing"), NewLead("closed"));

        await _context.SaveChangesAsync();
    }

    private static Plan NewPlan(Guid id, string name, string slug, string cycle, decimal price, string currency) => new()
    {
        Id = id, Name = name, Slug = slug, Tier = slug, BillingCycle = cycle, Price = price, Currency = currency,
        CreditsPerCycle = 1000, IsActive = true, CreatedAt = Utc(1, 1), UpdatedAt = Utc(1, 1),
    };

    private Subscription NewSub(
        Guid id, Guid workspaceId, Guid planId, DateTime created, DateTime periodEnd, int credits = 0,
        string status = "active", bool isActive = true, bool autoRenew = true, decimal? contract = null,
        DateTime? trialEndsAt = null, DateTime? cancelledAt = null,
        string serviceState = "healthy", string? suspendedReason = null) => new()
    {
        Id = id, UserId = _user, WorkspaceId = workspaceId, PlanId = planId, Status = status, IsActive = isActive,
        AutoRenew = autoRenew, ContractPriceVnd = contract, TrialEndsAt = trialEndsAt, CancelledAt = cancelledAt,
        CreditsRemaining = credits, CurrentPeriodStart = created, CurrentPeriodEnd = periodEnd,
        ServiceState = serviceState, SuspendedReason = suspendedReason,
        // updated_at deliberately "now": settlement bumps it on every charge, so nothing may date by it.
        CreatedAt = created, UpdatedAt = Now,
    };

    private Payment NewPayment(
        Guid subscriptionId, decimal total, string currency, string status, DateTime at, string providerTxId,
        string provider = "stripe", string method = "card", Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), SubscriptionId = subscriptionId, UserId = _user, Amount = total, TotalAmount = total,
        Currency = currency, Status = status, Provider = provider, PaymentMethod = method,
        ProviderTransactionId = providerTxId, PaidAt = status == "paid" ? at : null,
        CreatedAt = at, UpdatedAt = at,
    };

    private Invoice NewInvoice(Guid paymentId, decimal total, string status, DateTime dueAt) => new()
    {
        Id = Guid.NewGuid(), PaymentId = paymentId, UserId = _user, InvoiceNumber = $"INV-{Guid.NewGuid():N}"[..20],
        Subtotal = total, Total = total, Currency = "VND", Status = status, LineItems = "[]",
        IssuedAt = dueAt.AddDays(-14), DueAt = dueAt, CreatedAt = dueAt.AddDays(-14),
    };

    private UsageRecord NewUsage(Guid subscriptionId, Guid workspaceId, string usageType, string unit, decimal quantity, int credits, DateTime at) => new()
    {
        Id = Guid.NewGuid(), SubscriptionId = subscriptionId, UserId = _user, WorkspaceId = workspaceId,
        UsageType = usageType, Unit = unit, Quantity = quantity, CreditsConsumed = credits, RecordedAt = at,
    };

    private CreditTransaction NewTx(
        Guid subscriptionId, Guid workspaceId, int amount, string type, int balanceAfter, DateTime at,
        string? chargeType, Guid? rateCardId, Guid? usageRecordId) => new()
    {
        Id = Guid.NewGuid(), SubscriptionId = subscriptionId, UserId = _user, WorkspaceId = workspaceId,
        Amount = amount, Type = type, BalanceAfter = balanceAfter, ChargeType = chargeType,
        PricingRateCardId = rateCardId, UsageRecordId = usageRecordId, Currency = "CRD", Status = "committed", CreatedAt = at,
    };

    private static SalesInquiry NewLead(string status) => new()
    {
        Id = Guid.NewGuid(), FirstName = "A", LastName = "B", WorkEmail = $"{Guid.NewGuid():N}@example.com",
        Company = "Acme", RequestType = "sales", CurrentMonthlyMeetingVolume = "10", Consent = true, Status = status,
        CreatedAt = Utc(9, 1), UpdatedAt = Utc(9, 1),
    };

    private static bool DockerAvailable() => new DockerFactAttribute().Skip is null;

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
