using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
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
/// Dubbing provider cost from MEASURED Cartesia credits (subscription.provider_usage_daily), read back
/// through admin Insights from real PostgreSQL (Testcontainers — needs Docker).
///
/// WarpTalk consume rows (September, FX 25,000 VND/USD), on the CRD cards as the #425 migration costs
/// them — rate-card estimate 0.1078 USD:
///   Sep 2  dub   120 s standard  30 credits  0.0588 USD
///   Sep 3  clone  60 s clone     40 credits  0.0441 USD
///   Sep 4  old    10 s standard   3 credits  0.0049 USD
///
/// Cartesia, per UTC day (synced 1 Oct, after every September day closed):
///   Sep 2 1,600 credits · Sep 3 800 of which 100 are STT (so 700 dubbing) · Sep 4 200 · other days 0
///   → 2,500 dubbing credits × $0.0000392 = 0.098 USD = 2,450 VND.
/// </summary>
public sealed class CartesiaMeasuredDubbingCostTests : IAsyncLifetime
{
    private const string CrdCostMigration = "20260918090000_set_provider_cost_on_crd_rate_cards.sql";
    private const string UsageMigration = "20260918120000_add_provider_usage_daily_and_cartesia_price.sql";

    private static DateTime Utc(int m, int d, int h = 0) => new(2026, m, d, h, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = Utc(10, 2, 8);

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    private readonly Guid _plan = Guid.NewGuid(), _user = Guid.NewGuid(), _workspace = Guid.NewGuid(), _subscription = Guid.NewGuid();
    private readonly Guid _dubbing = Guid.NewGuid(), _clone = Guid.NewGuid(), _retiredDubbing = Guid.NewGuid();

    private BillingDbContext _context = null!;
    private CartesiaUsageSyncStatus _sync = null!;
    private AdminBillingInsightsService _service = null!;

    public async Task InitializeAsync()
    {
        if (!DockerAvailable()) return;

        await _database.StartAsync();
        _context = NewContext();
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
                Result.Success(ids.ToDictionary(id => id, _ => "Workspace")));
        _sync = new CartesiaUsageSyncStatus(configured: true, filteredToApiKey: true);
        _service = new AdminBillingInsightsService(
            new UnitOfWork(_context),
            new UsageRateCardRepository(_context),
            workspaces.Object,
            NullLogger<AdminBillingInsightsService>.Instance,
            new FixedTime(Now),
            _sync);

        await SeedAsync();
        await ApplyMigrationAsync(CrdCostMigration);
    }

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    // ── Measured ────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task EverySyncedDay_PricesDubbingFromCartesiaCredits_WithTheDefaultPriceWhenNoneIsConfigured()
    {
        await SeedSeptemberCartesiaAsync(firstDay: 1);
        await _sync.MarkSucceededAsync(Utc(10, 2, 7));

        var dto = await PeriodAsync(Utc(9, 1), Utc(10, 1), "UTC");

        var cost = M(dto, "aiProviderCost");
        cost.Value.Should().Be(2_450m, "2,500 Cartesia dubbing credits × $0.0000392 × 25,000 — not the 2,695 VND rate-card estimate");
        cost.Note.Should().Be(
            "covers 100% of consumed credits (3 of 3 transactions have a provider cost); "
            + "dubbing measured from Cartesia usage: 2,500 credits × $0.0000392 over 30 UTC days; USD converted at 25,000 VND/USD");
        M(dto, "grossMargin").Note.Should().BeNull();

        dto.AiProviderCostBasis.Should().BeEquivalentTo(new AdminAiProviderCostBasisDto(
            "measured", 30, 0, 2_500m, 0.0000392m, "ok"));
    }

    [DockerFact]
    public async Task TheConfiguredPrice_IsWhatACreditCosts()
    {
        await SeedSeptemberCartesiaAsync(firstDay: 1);
        _context.BillingPricingConfigs.Add(new BillingPricingConfig { Key = "cartesia_usd_per_credit", Value = 0.00002m, UpdatedAt = Utc(9, 1) });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var dto = await PeriodAsync(Utc(9, 1), Utc(10, 1), "UTC");

        M(dto, "aiProviderCost").Value.Should().Be(1_250m, "2,500 × $0.00002 = 0.05 USD");
        dto.AiProviderCostBasis!.CartesiaUsdPerCredit.Should().Be(0.00002m);
    }

    [DockerFact]
    public async Task DaysWithoutSyncedUsage_FallBackToTheRateCardEstimate_AndSaySo()
    {
        // Sep 1 and 2 were never synced: Sep 2's dub keeps its 0.0588 USD rate-card estimate.
        await SeedSeptemberCartesiaAsync(firstDay: 3);

        var dto = await PeriodAsync(Utc(9, 1), Utc(10, 1), "UTC");

        // 900 measured credits × 0.0000392 = 0.03528 USD, + 0.0588 USD estimated = 0.09408 USD.
        var cost = M(dto, "aiProviderCost");
        cost.Value.Should().Be(2_352m);
        cost.Note.Should().Be(
            "covers 100% of consumed credits (3 of 3 transactions have a provider cost); "
            + "dubbing measured from Cartesia usage: 900 credits × $0.0000392 over 28 of 30 UTC days; "
            + "estimated from rate cards (12.5 characters/s) on the other 2 days with no synced Cartesia usage; "
            + "USD converted at 25,000 VND/USD");
        dto.AiProviderCostBasis!.Basis.Should().Be("mixed");
        dto.AiProviderCostBasis.MeasuredDays.Should().Be(28);
        dto.AiProviderCostBasis.EstimatedDays.Should().Be(2);
    }

    [DockerFact]
    public async Task NoSyncedUsage_KeepsTheRateCardEstimate()
    {
        await _sync.MarkDisabledAsync(CartesiaUsageSyncStatus.NotConfiguredMessage);

        var dto = await PeriodAsync(Utc(9, 1), Utc(10, 1), "UTC");

        M(dto, "aiProviderCost").Value.Should().Be(2_695m, "the #425 per-second rate-card estimate");
        M(dto, "aiProviderCost").Note.Should().Contain(
            "dubbing estimated from rate cards (12.5 characters/s): no Cartesia usage synced for the period");
        dto.AiProviderCostBasis.Should().BeEquivalentTo(new AdminAiProviderCostBasisDto(
            "estimated", 0, 30, 0m, 0.0000392m, "disabled"));
    }

    [DockerFact]
    public async Task ALocalCalendarPeriod_ProRatesTheUtcDaysAtItsEdges()
    {
        // September in Vietnam is [31 Aug 17:00Z, 30 Sep 17:00Z): 7 h of 31 Aug and 17 h of 30 Sep.
        await SeedSeptemberCartesiaAsync(firstDay: 1);
        await SeedCartesiaDayAsync(new DateOnly(2026, 8, 31), 2_400, Utc(9, 2));
        await SeedCartesiaDayAsync(new DateOnly(2026, 9, 30), 240, Utc(10, 1), replace: true);

        var dto = await PeriodAsync(Utc(8, 31, 17), Utc(9, 30, 17), "Asia/Ho_Chi_Minh");

        // 2,400 × 7/24 = 700, + 2,500, + 240 × 17/24 = 170 → 3,370 credits = 0.132104 USD.
        var cost = M(dto, "aiProviderCost");
        cost.Value.Should().Be(3_303m);
        cost.Note.Should().Contain("dubbing measured from Cartesia usage: 3,370 credits × $0.0000392 over 31 UTC days");
        cost.Note.Should().Contain("Cartesia reports UTC days, so 2 days at the edges of the period are pro-rated by hours");
    }

    [DockerFact]
    public async Task ADayLastReadBeforeItClosed_IsEstimated()
    {
        await SeedSeptemberCartesiaAsync(firstDay: 1);
        // The worker was down after 20:00 on Sep 2: that day's 1,600 is a partial count.
        await SeedCartesiaDayAsync(new DateOnly(2026, 9, 2), 1_000, Utc(9, 2, 20), replace: true);

        var dto = await PeriodAsync(Utc(9, 1), Utc(10, 1), "UTC");

        // 900 measured + Sep 2's 0.0588 USD estimate.
        M(dto, "aiProviderCost").Value.Should().Be(2_352m);
        dto.AiProviderCostBasis!.EstimatedDays.Should().Be(1);
    }

    // ── Snapshot ────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Snapshot_ReportsThisUtcMonth_Today_TheSync_AndThatNoBalanceExists()
    {
        await SeedSeptemberCartesiaAsync(firstDay: 1);
        await SeedCartesiaDayAsync(new DateOnly(2026, 10, 1), 300, Utc(10, 2, 6));
        await SeedCartesiaDayAsync(new DateOnly(2026, 10, 2), 50, Utc(10, 2, 7));
        await _sync.MarkSucceededAsync(Utc(10, 2, 7));

        var result = await _service.GetSnapshotAsync("Asia/Ho_Chi_Minh");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Cartesia.Should().BeEquivalentTo(new AdminCartesiaUsageDto(
            "ok",
            null,
            true,
            350,
            50,
            null,
            AdminBillingInsightsService.CartesiaRemainingCreditsNote,
            Utc(10, 2, 7),
            Utc(10, 2, 7),
            0.0000392m));
    }

    [DockerFact]
    public async Task Snapshot_WhenNeverSyncedAndDisabled_SaysWhyAndReportsNoFigures()
    {
        await _sync.MarkDisabledAsync(CartesiaUsageSyncStatus.NotConfiguredMessage);

        var cartesia = (await _service.GetSnapshotAsync(null)).Value!.Cartesia!;

        cartesia.Status.Should().Be("disabled");
        cartesia.StatusNote.Should().Contain("CARTESIA_ADMIN_API_KEY is not set");
        cartesia.CreditsThisMonth.Should().BeNull();
        cartesia.CreditsToday.Should().BeNull();
        cartesia.LastSyncedAt.Should().BeNull();
        cartesia.RemainingCredits.Should().BeNull();
    }

    // ── The table and the migration ─────────────────────────────────────────

    [DockerFact]
    public async Task ReplaceWindow_UpsertsOnTheNaturalKey_AndDropsGroupsTheProviderNoLongerReports()
    {
        var repository = new ProviderUsageDailyRepository(_context);
        var day = new DateOnly(2026, 9, 10);
        string[] kinds = { "total", "capability" };

        await repository.ReplaceWindowAsync("cartesia", day, day, kinds,
            new[] { Row(day, "total", "all", 100), Row(day, "capability", "tts", 90), Row(day, "capability", "stt", 10) },
            Utc(9, 11));
        await repository.ReplaceWindowAsync("cartesia", day, day, kinds,
            new[] { Row(day, "total", "all", 120), Row(day, "capability", "tts", 120) },
            Utc(9, 11, 1));

        var rows = await _context.ProviderUsageDaily.AsNoTracking().OrderBy(r => r.GroupKind).ThenBy(r => r.GroupId).ToListAsync();
        rows.Select(r => (r.GroupKind, r.GroupId, r.Credits)).Should().Equal(
            ("capability", "tts", 120L),
            ("total", "all", 120L));
        rows.Should().OnlyContain(r => r.SyncedAt == Utc(9, 11, 1));
    }

    [DockerFact]
    public async Task Migration_WidensTheConfigValue_SeedsThePrice_AndIsIdempotent()
    {
        // As production has it before the migration: (18,6), which cannot hold 0.0000392.
        await _context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE subscription.billing_pricing_config ALTER COLUMN value TYPE numeric(18, 6);");
        await _context.Database.ExecuteSqlRawAsync("DROP TABLE subscription.provider_usage_daily;");

        await ApplyMigrationAsync(UsageMigration);
        await ApplyMigrationAsync(UsageMigration);

        (await _context.BillingPricingConfigs.AsNoTracking().SingleAsync(c => c.Key == "cartesia_usd_per_credit"))
            .Value.Should().Be(0.0000392m);
        (await _context.BillingPricingConfigs.AsNoTracking().SingleAsync(c => c.Key == "fx_rate_usd_vnd"))
            .Value.Should().Be(25_000m, "widening keeps every existing value");

        // The table the migration created is the one the repository writes.
        var day = new DateOnly(2026, 9, 10);
        await new ProviderUsageDailyRepository(_context).ReplaceWindowAsync(
            "cartesia", day, day, new[] { "total" }, new[] { Row(day, "total", "all", 5) }, Utc(9, 11));
        (await _context.ProviderUsageDaily.AsNoTracking().SingleAsync()).Credits.Should().Be(5);
    }

    // ── Seed ────────────────────────────────────────────────────────────────

    private async Task SeedAsync()
    {
        _context.UsageRateCards.AddRange(
            Crd(_dubbing, "AUDIO_DUBBING_STANDARD", 0.25m),
            Crd(_clone, "AUDIO_DUBBING_VOICE_CLONE", 0.666667m),
            Crd(_retiredDubbing, "AUDIO_DUBBING_STANDARD", 0.2m, from: Utc(7, 1), to: Utc(7, 27)));
        _context.BillingPricingConfigs.Add(new BillingPricingConfig { Key = "fx_rate_usd_vnd", Value = 25_000m, UpdatedAt = Utc(1, 1) });
        _context.Plans.Add(new Plan
        {
            Id = _plan, Name = "Team", Slug = "team", Tier = "team", BillingCycle = "monthly", Price = 500_000m,
            Currency = "VND", CreditsPerCycle = 1000, IsActive = true, CreatedAt = Utc(1, 1), UpdatedAt = Utc(1, 1),
        });
        _context.Subscriptions.Add(new Subscription
        {
            Id = _subscription, UserId = _user, WorkspaceId = _workspace, PlanId = _plan, Status = "active", IsActive = true,
            AutoRenew = true, CreditsRemaining = 1000, CurrentPeriodStart = Utc(9, 1), CurrentPeriodEnd = Utc(10, 1),
            ServiceState = "healthy", CreatedAt = Utc(8, 1), UpdatedAt = Utc(8, 1),
        });

        var balance = 1000;
        void Charge(string chargeType, Guid card, decimal seconds, int credits, DateTime at)
        {
            var usage = new UsageRecord
            {
                Id = Guid.NewGuid(), SubscriptionId = _subscription, UserId = _user, WorkspaceId = _workspace,
                UsageType = chargeType, Unit = "second", Quantity = seconds, CreditsConsumed = credits, RecordedAt = at,
            };
            balance -= credits;
            _context.UsageRecords.Add(usage);
            _context.CreditTransactions.Add(new CreditTransaction
            {
                Id = Guid.NewGuid(), SubscriptionId = _subscription, UserId = _user, WorkspaceId = _workspace,
                Amount = -credits, Type = "consume", BalanceAfter = balance, ChargeType = chargeType,
                PricingRateCardId = card, UsageRecordId = usage.Id, Currency = "CRD", Status = "committed", CreatedAt = at,
            });
        }

        Charge("AUDIO_DUBBING_STANDARD", _dubbing, 120m, 30, Utc(9, 2, 10));
        Charge("AUDIO_DUBBING_VOICE_CLONE", _clone, 60m, 40, Utc(9, 3, 10));
        Charge("AUDIO_DUBBING_STANDARD", _retiredDubbing, 10m, 3, Utc(9, 4, 10));

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>September as the sync writes it, from <paramref name="firstDay"/> on, read on 1 Oct.</summary>
    private async Task SeedSeptemberCartesiaAsync(int firstDay)
    {
        for (var day = firstDay; day <= 30; day++)
        {
            var date = new DateOnly(2026, 9, day);
            var credits = day switch { 2 => 1_600, 3 => 800, 4 => 200, _ => 0 };
            _context.ProviderUsageDaily.Add(Row(date, "total", "all", credits, Utc(10, 1)));
            if (day == 2) _context.ProviderUsageDaily.Add(Row(date, "model", "sonic-3.5", 1_600, Utc(10, 1)));
            if (day == 3)
            {
                _context.ProviderUsageDaily.Add(Row(date, "capability", "tts", 700, Utc(10, 1)));
                _context.ProviderUsageDaily.Add(Row(date, "capability", "stt", 100, Utc(10, 1)));
            }
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedCartesiaDayAsync(DateOnly date, long credits, DateTime syncedAt, bool replace = false)
    {
        if (replace)
        {
            await _context.ProviderUsageDaily
                .Where(r => r.UsageDate == date && r.GroupKind == "total")
                .ExecuteDeleteAsync();
        }

        _context.ProviderUsageDaily.Add(Row(date, "total", "all", credits, syncedAt));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static ProviderUsageDaily Row(DateOnly date, string kind, string id, long credits, DateTime? syncedAt = null) => new()
    {
        Id = Guid.NewGuid(), Provider = "cartesia", UsageDate = date, GroupKind = kind, GroupId = id,
        Credits = credits, SyncedAt = syncedAt ?? Utc(10, 1),
    };

    private static UsageRateCard Crd(Guid id, string chargeType, decimal unitPrice, DateTime? from = null, DateTime? to = null) => new()
    {
        Id = id, ChargeType = chargeType, Currency = "CRD", UnitPrice = unitPrice,
        EffectiveFrom = from ?? Utc(7, 27), EffectiveTo = to, IsActive = to is null,
    };

    private async Task ApplyMigrationAsync(string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(FindBackendRoot(), "billing/database/migrations", file));
        await using var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        _context.ChangeTracker.Clear();
    }

    private BillingDbContext NewContext() => new(new DbContextOptionsBuilder<BillingDbContext>()
        .UseNpgsql(_database.GetConnectionString()).Options);

    private async Task<AdminBillingInsightsDto> PeriodAsync(DateTime from, DateTime to, string tz)
    {
        var result = await _service.GetInsightsAsync(new AdminInsightsQuery { From = from, To = to, Compare = "previous", Tz = tz });
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    private static AdminInsightMetric M(AdminBillingInsightsDto dto, string id) => dto.Metrics.Single(m => m.Id == id);

    private static string FindBackendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate backend repository root.");
    }

    private static bool DockerAvailable() => new DockerFactAttribute().Skip is null;

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
