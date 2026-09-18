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
/// AI provider cost on the cards production actually settles on. billing_worker charges every event
/// on an internal credit-unit (CRD) card, and 001/016 seeded those with no unit and no provider cost,
/// so Insights covered ~0% of real usage. These tests seed the CRD cards exactly as 001/016 left them,
/// run the real migration SQL over them (twice — it must be idempotent), and read Insights back from
/// PostgreSQL (Testcontainers — needs Docker).
///
/// Consume rows (September, FX 25,000 VND/USD), all on CRD cards:
///   dub   120 s standard  30 credits   0.00049  USD/s → 0.0588 USD
///   clone  60 s clone     40 credits   0.000735 USD/s → 0.0441 USD
///   old    10 s standard   3 credits   RETIRED standard card, same model → 0.0049 USD
///   → 0.1078 USD = 2,695 VND over 73 credits, 100% covered.
/// </summary>
public sealed class CrdRateCardProviderCostTests : IAsyncLifetime
{
    private const string MigrationFile = "20260918090000_set_provider_cost_on_crd_rate_cards.sql";

    private static DateTime Utc(int m, int d, int h = 0) => new(2026, m, d, h, 0, 0, DateTimeKind.Utc);

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    private readonly Guid _plan = Guid.NewGuid(), _user = Guid.NewGuid(), _workspace = Guid.NewGuid(), _subscription = Guid.NewGuid();

    private readonly Guid _stt = Guid.NewGuid(), _translation = Guid.NewGuid(), _dubbing = Guid.NewGuid(),
        _clone = Guid.NewGuid(), _assistant = Guid.NewGuid(), _retiredDubbing = Guid.NewGuid(), _vndDubbing = Guid.NewGuid();

    private BillingDbContext _context = null!;
    private AdminBillingInsightsService _service = null!;

    public async Task InitializeAsync()
    {
        if (!DockerAvailable()) return;

        await _database.StartAsync();
        _context = new BillingDbContext(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(_database.GetConnectionString()).Options);
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
        _service = new AdminBillingInsightsService(
            new UnitOfWork(_context),
            new UsageRateCardRepository(_context),
            workspaces.Object,
            NullLogger<AdminBillingInsightsService>.Instance,
            new FixedTime(Utc(10, 2)));

        await SeedCardsAsProductionHasThemAsync();
    }

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    // ── The migration ────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Migration_CostsTheCrdCardsWithARealPrice_AndIsIdempotent()
    {
        await ApplyMigrationAsync();
        await ApplyMigrationAsync();

        var cards = await _context.UsageRateCards.AsNoTracking().ToDictionaryAsync(c => c.Id);

        cards[_stt].Should().BeEquivalentTo(new { Unit = "second", ProviderUnitCost = 0.00005m });
        cards[_dubbing].Should().BeEquivalentTo(new { Unit = "second", ProviderUnitCost = 0.00049m });
        cards[_clone].Should().BeEquivalentTo(new { Unit = "second", ProviderUnitCost = 0.000735m });
        // Retired, but past usage references it and it priced the same model.
        cards[_retiredDubbing].Should().BeEquivalentTo(new { Unit = "second", ProviderUnitCost = 0.00049m, IsActive = false });

        // No real per-second price exists for these; they stay honest blanks.
        cards[_translation].Should().BeEquivalentTo(new { Unit = "second", ProviderUnitCost = (decimal?)null });
        cards[_assistant].Should().BeEquivalentTo(new { Unit = "token", ProviderUnitCost = (decimal?)null });

        // What settlement reads is untouched, and so is every VND card.
        cards[_dubbing].Should().BeEquivalentTo(new
        {
            UnitPrice = 0.25m, Currency = "CRD", Provider = (string?)null, Model = (string?)null,
            EffectiveTo = (DateTime?)null, IsActive = true,
        });
        cards[_vndDubbing].Should().BeEquivalentTo(new { Unit = "character", ProviderUnitCost = 0.0000392m, Notes = "seeded by 006" });

        // One note per costed card, even after a second run.
        cards[_dubbing].Notes.Should().StartWith("Provider cost: Cartesia sonic-3.5 ");
        cards[_dubbing].Notes.Should().NotContain(" | ");
    }

    [DockerFact]
    public async Task Migration_NeverOverwritesACostAnAdminAlreadyEntered()
    {
        await using (var tracked = NewContext())
        {
            var card = await tracked.UsageRateCards.SingleAsync(c => c.Id == _dubbing);
            card.Unit = "second";
            card.ProviderUnitCost = 0.0006m;
            await tracked.SaveChangesAsync();
        }

        await ApplyMigrationAsync();

        (await _context.UsageRateCards.AsNoTracking().SingleAsync(c => c.Id == _dubbing))
            .ProviderUnitCost.Should().Be(0.0006m);
    }

    // ── Insights over the migrated cards ────────────────────────────────────

    [DockerFact]
    public async Task Insights_CoverAllDubbingUsage_OnCrdCards()
    {
        await ApplyMigrationAsync();
        await SeedConsumptionAsync(withTranslation: false);

        var dto = await SeptemberAsync();

        var cost = M(dto, "aiProviderCost");
        cost.Value.Should().Be(2_695m);
        cost.Note.Should().Be(
            "covers 100% of consumed credits (3 of 3 transactions have a provider cost); USD converted at 25,000 VND/USD");

        var margin = M(dto, "grossMargin");
        margin.Value.Should().Be(-2_695m, "no payments in the period, so the margin is the cost, negated");
        margin.Note.Should().BeNull("every consumed credit carries a cost");
    }

    [DockerFact]
    public async Task Insights_NameTheChargeTypeThatHasNoPrice()
    {
        await ApplyMigrationAsync();
        await SeedConsumptionAsync(withTranslation: true);

        var dto = await SeptemberAsync();

        // Translation adds 27 credits with no provider cost: 73 of 100 credits are covered.
        var cost = M(dto, "aiProviderCost");
        cost.Value.Should().Be(2_695m);
        cost.Note.Should().Be(
            "covers 73% of consumed credits (3 of 4 transactions have a provider cost); no provider cost for TRANSLATION (27%); USD converted at 25,000 VND/USD");
        M(dto, "grossMargin").Note.Should().Be(
            "AI cost covers only 73% of consumed credits (no provider cost for TRANSLATION (27%)), so this margin is overstated");
    }

    [DockerFact]
    public async Task Insights_BeforeTheMigration_ExplainWhyNothingIsCovered()
    {
        await SeedConsumptionAsync(withTranslation: true);

        var cost = M(await SeptemberAsync(), "aiProviderCost");

        cost.Value.Should().BeNull();
        cost.Note.Should().Be(
            "cannot be reconstructed: none of the 4 consume transaction(s) was settled on a rate card with a provider_unit_cost (no provider cost for AUDIO_DUBBING_VOICE_CLONE (40%), AUDIO_DUBBING_STANDARD (33%), TRANSLATION (27%))");
    }

    // ── The admin write path ────────────────────────────────────────────────

    [DockerFact]
    public async Task SetProviderCost_RecordsAMissingCostOnTheCard_SoSettledUsageIsCovered()
    {
        await ApplyMigrationAsync();
        await SeedConsumptionAsync(withTranslation: true);

        var outcome = await new UsageRateCardRepository(_context).SetCreditRateCardProviderCostAsync(_translation, 0.0001m);

        outcome!.Change.Should().Be(RateCardProviderCostChange.Recorded);
        outcome.Card.Id.Should().Be(_translation);
        _context.ChangeTracker.Clear();

        // 108 s of translated speech × 0.0001 = 0.0108 USD = 270 VND on top of the dubbing.
        var cost = M(await SeptemberAsync(), "aiProviderCost");
        cost.Value.Should().Be(2_965m);
        cost.Note.Should().StartWith("covers 100% of consumed credits (4 of 4 transactions");
    }

    [DockerFact]
    public async Task SetProviderCost_SupersedesAChangedCost_SoSettledUsageKeepsTheOldOne()
    {
        await ApplyMigrationAsync();
        await SeedConsumptionAsync(withTranslation: false);

        var outcome = await new UsageRateCardRepository(_context).SetCreditRateCardProviderCostAsync(_dubbing, 0.0006m);

        outcome!.Change.Should().Be(RateCardProviderCostChange.Superseded);
        outcome.Card.Id.Should().NotBe(_dubbing);
        outcome.Card.Should().BeEquivalentTo(new
        {
            ChargeType = "AUDIO_DUBBING_STANDARD", Unit = "second", Currency = "CRD", UnitPrice = 0.25m,
            ProviderUnitCostUsd = 0.0006m, IsActive = true, EffectiveTo = (DateTime?)null,
        });
        _context.ChangeTracker.Clear();

        var old = await _context.UsageRateCards.AsNoTracking().SingleAsync(c => c.Id == _dubbing);
        old.Should().BeEquivalentTo(new { IsActive = false, ProviderUnitCost = 0.00049m });
        old.EffectiveTo.Should().NotBeNull();

        // September was settled on the old card, so it keeps the old price.
        M(await SeptemberAsync(), "aiProviderCost").Value.Should().Be(2_695m);

        // Same price again is a no-op, not another version.
        var again = await new UsageRateCardRepository(_context).SetCreditRateCardProviderCostAsync(outcome.Card.Id, 0.0006m);
        again!.Change.Should().Be(RateCardProviderCostChange.Unchanged);
        (await _context.UsageRateCards.CountAsync(c => c.ChargeType == "AUDIO_DUBBING_STANDARD" && c.Currency == "CRD"))
            .Should().Be(3);
    }

    [DockerFact]
    public async Task SetProviderCost_RefusesVndRetiredAndUnitlessCards()
    {
        var repository = new UsageRateCardRepository(_context);

        (await repository.SetCreditRateCardProviderCostAsync(_vndDubbing, 0.0001m))!.Change
            .Should().Be(RateCardProviderCostChange.Refused, "a VND card's credit price is derived from its cost");
        (await repository.SetCreditRateCardProviderCostAsync(_retiredDubbing, 0.0001m))!.Change
            .Should().Be(RateCardProviderCostChange.Refused);
        // Before the migration the CRD cards have no unit.
        (await repository.SetCreditRateCardProviderCostAsync(_dubbing, 0.0001m))!.Change
            .Should().Be(RateCardProviderCostChange.Refused);
        (await repository.SetCreditRateCardProviderCostAsync(Guid.NewGuid(), 0.0001m)).Should().BeNull();

        _context.ChangeTracker.Clear();
        (await _context.UsageRateCards.AsNoTracking().Where(c => c.ProviderUnitCost == 0.0001m).CountAsync()).Should().Be(0);
    }

    // ── Seed ────────────────────────────────────────────────────────────────

    /// <summary>The CRD cards as 001 and 016 inserted them — no unit, provider, model or cost — plus a VND card from 006.</summary>
    private async Task SeedCardsAsProductionHasThemAsync()
    {
        _context.UsageRateCards.AddRange(
            Crd(_stt, "STT", 0.25m),
            Crd(_translation, "TRANSLATION", 0.25m),
            Crd(_dubbing, "AUDIO_DUBBING_STANDARD", 0.25m),
            Crd(_clone, "AUDIO_DUBBING_VOICE_CLONE", 0.666667m),
            Crd(_assistant, "AI_ASSISTANT", 0.02m, from: Utc(8, 2)),
            Crd(_retiredDubbing, "AUDIO_DUBBING_STANDARD", 0.2m, from: Utc(7, 1), to: Utc(7, 27)),
            new UsageRateCard
            {
                Id = _vndDubbing, ChargeType = "AUDIO_DUBBING_STANDARD", Unit = "character", Currency = "VND",
                Provider = "cartesia", Model = "sonic-3.5", UnitPrice = 0.773220m, ProviderUnitCost = 0.0000392m,
                MarkupMultiplier = 3m, EffectiveFrom = Utc(7, 26), IsActive = true, Notes = "seeded by 006",
            });

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

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>Consume rows exactly as settle_usage_charge writes them: usage record + transaction on the card.</summary>
    private async Task SeedConsumptionAsync(bool withTranslation)
    {
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

        Charge("AUDIO_DUBBING_STANDARD", _dubbing, 120m, 30, Utc(9, 2));
        Charge("AUDIO_DUBBING_VOICE_CLONE", _clone, 60m, 40, Utc(9, 3));
        Charge("AUDIO_DUBBING_STANDARD", _retiredDubbing, 10m, 3, Utc(9, 4));
        if (withTranslation) Charge("TRANSLATION", _translation, 108m, 27, Utc(9, 5));

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static UsageRateCard Crd(Guid id, string chargeType, decimal unitPrice, DateTime? from = null, DateTime? to = null) => new()
    {
        Id = id, ChargeType = chargeType, Currency = "CRD", UnitPrice = unitPrice,
        EffectiveFrom = from ?? Utc(7, 27), EffectiveTo = to, IsActive = to is null,
    };

    /// <summary>Runs the migration file as the production runner does: the whole file, one transaction.</summary>
    private async Task ApplyMigrationAsync()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(FindBackendRoot(), "billing/database/migrations", MigrationFile));
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

    private async Task<AdminBillingInsightsDto> SeptemberAsync()
    {
        var result = await _service.GetInsightsAsync(new AdminInsightsQuery
        {
            From = Utc(9, 1), To = Utc(10, 1), Compare = "previous", Tz = "UTC",
        });
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
