using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence.Contexts;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// Runs against real PostgreSQL: the analytics and ledger queries are aggregations, and an
/// in-memory provider would happily evaluate LINQ that PostgreSQL cannot translate (WT-206).
/// </summary>
public sealed class AdminWorkspaceAnalyticsServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _otherWorkspaceId = Guid.NewGuid();
    private readonly Guid _subscriptionId = Guid.NewGuid();
    private readonly Guid _otherSubscriptionId = Guid.NewGuid();
    private readonly Guid _planId = Guid.NewGuid();
    private readonly Guid _roomA = Guid.NewGuid();
    private readonly Guid _roomB = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private BillingDbContext _context = null!;
    private AdminWorkspaceAnalyticsService _service = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _context = new BillingDbContext(
            new DbContextOptionsBuilder<BillingDbContext>()
                .UseNpgsql(_database.GetConnectionString())
                .Options);

        // The schema declares uuidv7() defaults; postgres:16 has no such builtin, so provide
        // the same shim the workspace integration tests use before EnsureCreated runs DDL.
        await _context.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION uuidv7() RETURNS uuid AS $$
            DECLARE
                timestamp_ms bigint;
                timestamp_hex text;
                uuid_hex text;
            BEGIN
                timestamp_ms := (extract(epoch from clock_timestamp()) * 1000)::bigint;
                timestamp_hex := lpad(to_hex(timestamp_ms), 12, '0');
                uuid_hex := timestamp_hex || '7' || lpad(to_hex((random() * 4095)::integer), 3, '0')
                    || '8' || lpad(to_hex((random() * 4095)::integer), 3, '0')
                    || lpad(to_hex((random() * 281474976710655)::bigint), 12, '0');
                RETURN uuid_hex::uuid;
            END;
            $$ LANGUAGE plpgsql;
            """);
        await _context.Database.EnsureCreatedAsync();

        _service = new AdminWorkspaceAnalyticsService(
            new UnitOfWork(_context), NullLogger<AdminWorkspaceAnalyticsService>.Instance);

        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        _context.Plans.Add(new Plan
        {
            Id = _planId,
            Name = "Enterprise",
            Slug = "enterprise",
            Tier = "enterprise",
            BillingCycle = "monthly",
            Price = 100,
            Currency = "USD",
            CreditsPerCycle = 1000,
            IsActive = true,
            CreatedAt = Anchor,
            UpdatedAt = Anchor,
        });

        _context.Subscriptions.AddRange(
            NewSubscription(_subscriptionId, _workspaceId, creditsRemaining: 640, usedThisCycle: 360),
            NewSubscription(_otherSubscriptionId, _otherWorkspaceId, creditsRemaining: 10, usedThisCycle: 5));

        // Two rooms, two features, three days — plus one row for a different workspace that
        // must never appear in this workspace's figures.
        _context.UsageRecords.AddRange(
            NewUsage(_subscriptionId, _workspaceId, "voice_translation", 100, 2, Anchor.AddDays(1), _roomA),
            NewUsage(_subscriptionId, _workspaceId, "voice_translation", 60, 1, Anchor.AddDays(1).AddHours(3), _roomA),
            NewUsage(_subscriptionId, _workspaceId, "summary", 40, 1, Anchor.AddDays(2), _roomB),
            NewUsage(_subscriptionId, _workspaceId, "text_to_speech", 160, 4, Anchor.AddDays(3), _roomB),
            NewUsage(_otherSubscriptionId, _otherWorkspaceId, "voice_translation", 999, 9, Anchor.AddDays(1), Guid.NewGuid()),
            // Outside the window under test.
            NewUsage(_subscriptionId, _workspaceId, "voice_translation", 500, 5, Anchor.AddDays(40), _roomA));

        _context.CreditTransactions.AddRange(
            NewTransaction(_subscriptionId, _workspaceId, -100, "consume", Anchor.AddDays(1), _roomA, balanceAfter: 900),
            NewTransaction(_subscriptionId, _workspaceId, -60, "consume", Anchor.AddDays(1).AddHours(3), _roomA, balanceAfter: 840),
            NewTransaction(_subscriptionId, _workspaceId, 500, "topup", Anchor.AddDays(2), null, balanceAfter: 1340),
            NewTransaction(_subscriptionId, _workspaceId, -40, "consume", Anchor.AddDays(2).AddHours(2), _roomB, balanceAfter: 1300),
            NewTransaction(_otherSubscriptionId, _otherWorkspaceId, -999, "consume", Anchor.AddDays(1), null, balanceAfter: 1));

        await _context.SaveChangesAsync();
    }

    private Subscription NewSubscription(Guid id, Guid workspaceId, int creditsRemaining, int usedThisCycle) => new()
    {
        Id = id,
        UserId = _userId,
        WorkspaceId = workspaceId,
        PlanId = _planId,
        Status = "active",
        CreditsRemaining = creditsRemaining,
        CreditsUsedThisCycle = usedThisCycle,
        CurrentPeriodStart = Anchor,
        CurrentPeriodEnd = Anchor.AddDays(30),
        IsActive = true,
        CreatedAt = Anchor,
        UpdatedAt = Anchor,
    };

    private UsageRecord NewUsage(
        Guid subscriptionId, Guid workspaceId, string usageType, int credits,
        decimal quantity, DateTime recordedAt, Guid? roomId) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = subscriptionId,
        UserId = _userId,
        WorkspaceId = workspaceId,
        UsageType = usageType,
        Unit = "request",
        Quantity = quantity,
        CreditsConsumed = credits,
        RecordedAt = recordedAt,
        TranslationRoomId = roomId,
    };

    private CreditTransaction NewTransaction(
        Guid subscriptionId, Guid workspaceId, int amount, string type,
        DateTime createdAt, Guid? referenceId, int balanceAfter) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = subscriptionId,
        UserId = _userId,
        WorkspaceId = workspaceId,
        Amount = amount,
        Type = type,
        Description = $"{type} entry",
        ReferenceId = referenceId,
        ReferenceType = referenceId is null ? null : "translation_room",
        BalanceAfter = balanceAfter,
        Currency = "USD",
        Status = "committed",
        CreatedAt = createdAt,
    };

    private static AdminDateRange Window => new() { From = Anchor, To = Anchor.AddDays(10) };

    // ── Authorization ────────────────────────────────────────

    [Fact]
    public void ControllerIsGatedOnTheSharedSystemAdminPolicy()
    {
        var authorize = typeof(AdminWorkspaceAnalyticsController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .SingleOrDefault();

        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be(SystemAdminAuthorization.PolicyName);
        authorize.Roles.Should().BeNull();
    }

    [Fact]
    public void ServiceExposesNoLedgerWriteOperation()
    {
        typeof(WarpTalk.BillingService.Application.Interfaces.IAdminWorkspaceAnalyticsService)
            .GetMethods()
            .Select(method => method.Name)
            .Should().OnlyContain(name => name.StartsWith("Get", StringComparison.Ordinal));
    }

    // ── Scoping ──────────────────────────────────────────────

    [Fact]
    public async Task AnalyticsCountsOnlyTheRequestedWorkspace()
    {
        var result = await _service.GetAnalyticsAsync(_workspaceId, Window);

        result.IsSuccess.Should().BeTrue();
        // 100 + 60 + 40 + 160 — the other workspace's 999 and the out-of-window 500 are absent.
        result.Value!.CreditsConsumedInPeriod.Should().Be(360);
        result.Value.CreditsToppedUpInPeriod.Should().Be(500);
        result.Value.MeetingsWithBillableUsage.Should().Be(2);
        result.Value.DistinctUsersBilled.Should().Be(1);
    }

    [Fact]
    public async Task LedgerReturnsOnlyTheRequestedWorkspace()
    {
        var result = await _service.GetCreditTransactionsAsync(
            _workspaceId, new AdminCreditTransactionQuery());

        result.Value!.Total.Should().Be(4);
        result.Value.Items.Should().OnlyContain(item => item.Amount != -999);
    }

    // ── Reconciliation ───────────────────────────────────────

    [Fact]
    public async Task SeriesAndBreakdownReconcileWithTheHeadlineTotal()
    {
        var analytics = (await _service.GetAnalyticsAsync(_workspaceId, Window)).Value!;

        analytics.ConsumptionSeries.Sum(point => point.CreditsConsumed)
            .Should().Be(analytics.CreditsConsumedInPeriod);
        analytics.FeatureBreakdown.Sum(feature => feature.CreditsConsumed)
            .Should().Be(analytics.CreditsConsumedInPeriod);
        analytics.ConsumptionSeries.Sum(point => point.Events)
            .Should().Be(analytics.FeatureBreakdown.Sum(feature => feature.Events));
    }

    [Fact]
    public async Task SeriesGroupsByDayInChronologicalOrder()
    {
        var analytics = (await _service.GetAnalyticsAsync(_workspaceId, Window)).Value!;

        analytics.ConsumptionSeries.Should().HaveCount(3);
        analytics.ConsumptionSeries.Select(point => point.Date)
            .Should().BeInAscendingOrder();
        // Both of day one's records land on the same point.
        analytics.ConsumptionSeries[0].CreditsConsumed.Should().Be(160);
        analytics.ConsumptionSeries[0].Events.Should().Be(2);
    }

    [Fact]
    public async Task BreakdownIsOrderedByCreditsDescending()
    {
        var analytics = (await _service.GetAnalyticsAsync(_workspaceId, Window)).Value!;

        analytics.FeatureBreakdown.Select(feature => feature.UsageType)
            .Should().ContainInOrder("text_to_speech", "voice_translation", "summary");
    }

    // ── Empty vs unavailable ─────────────────────────────────

    [Fact]
    public async Task WorkspaceWithoutASubscriptionReportsNoBillingRatherThanZero()
    {
        var analytics = (await _service.GetAnalyticsAsync(Guid.NewGuid(), Window)).Value!;

        analytics.Credits.SubscriptionFound.Should().BeFalse();
        analytics.Credits.CreditsRemaining.Should().BeNull();
        analytics.CreditsConsumedInPeriod.Should().Be(0);
        analytics.ConsumptionSeries.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkspaceWithASubscriptionReportsItsBalance()
    {
        var analytics = (await _service.GetAnalyticsAsync(_workspaceId, Window)).Value!;

        analytics.Credits.SubscriptionFound.Should().BeTrue();
        analytics.Credits.CreditsRemaining.Should().Be(640);
        analytics.Credits.CreditsUsedThisCycle.Should().Be(360);
        analytics.Credits.PlanId.Should().Be(_planId);
    }

    // ── Filters, ordering, paging ────────────────────────────

    [Fact]
    public async Task LedgerFiltersByTypeReferenceAndAmount()
    {
        var byType = await _service.GetCreditTransactionsAsync(
            _workspaceId, new AdminCreditTransactionQuery { Type = "TOPUP" });
        byType.Value!.Items.Should().ContainSingle().Which.Amount.Should().Be(500);

        var byReference = await _service.GetCreditTransactionsAsync(
            _workspaceId, new AdminCreditTransactionQuery { ReferenceId = _roomA });
        byReference.Value!.Total.Should().Be(2);

        var byAmount = await _service.GetCreditTransactionsAsync(
            _workspaceId, new AdminCreditTransactionQuery { MinAmount = -60, MaxAmount = 0 });
        byAmount.Value!.Items.Should().OnlyContain(item => item.Amount >= -60 && item.Amount <= 0);
    }

    [Fact]
    public async Task LedgerRejectsUnknownTypeAndInvertedRanges()
    {
        (await _service.GetCreditTransactionsAsync(
                _workspaceId, new AdminCreditTransactionQuery { Type = "teleport" }))
            .ErrorCode.Should().Be(ErrorCodes.ValidationError);

        (await _service.GetCreditTransactionsAsync(
                _workspaceId, new AdminCreditTransactionQuery { From = Anchor.AddDays(5), To = Anchor }))
            .ErrorCode.Should().Be(ErrorCodes.ValidationError);

        (await _service.GetCreditTransactionsAsync(
                _workspaceId, new AdminCreditTransactionQuery { MinAmount = 10, MaxAmount = 1 }))
            .ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task LedgerIsNewestFirstAndPagesWithoutOverlap()
    {
        var first = await _service.GetCreditTransactionsAsync(
            _workspaceId, new AdminCreditTransactionQuery { Page = 1, PageSize = 2 });
        var second = await _service.GetCreditTransactionsAsync(
            _workspaceId, new AdminCreditTransactionQuery { Page = 2, PageSize = 2 });

        first.Value!.Total.Should().Be(4);
        first.Value.Items.Select(item => item.CreatedAt).Should().BeInDescendingOrder();
        first.Value.Items.Select(item => item.Id)
            .Should().NotIntersectWith(second.Value!.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task LedgerExposesSignedAmountsAndBalanceAfter()
    {
        var page = await _service.GetCreditTransactionsAsync(
            _workspaceId, new AdminCreditTransactionQuery { Type = "consume" });

        page.Value!.Items.Should().OnlyContain(item => item.Amount < 0);
        page.Value.Items.Should().OnlyContain(item => item.BalanceAfter > 0);
        page.Value.Items.Should().OnlyContain(item => item.Description != null);
    }

    [Fact]
    public async Task AnalyticsRejectsAnExcessiveWindow()
    {
        var result = await _service.GetAnalyticsAsync(
            _workspaceId, new AdminDateRange { From = Anchor.AddYears(-3), To = Anchor });

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }
}
