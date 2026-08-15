using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.BillingService.Tests.Infrastructure.Repositories;

/// <summary>
/// Runs against real PostgreSQL, for the reason AdminWorkspaceAnalyticsServiceTests already
/// documents (WT-206): an in-memory provider happily evaluates LINQ that PostgreSQL cannot
/// translate, so an aggregation covered only by a mocked repository is not covered at all.
///
/// This endpoint proved it. <c>GetUsageByMemberAsync</c> ordered a POSITIONAL RECORD projection
/// by one of its own properties. A positional record's properties come from constructor
/// parameters rather than member bindings, so EF Core could not map <c>u.CreditsConsumed</c>
/// back to the <c>g.Sum(...)</c> it came from; the whole tree failed to translate, the service's
/// catch-all turned it into a 500, and the dashboard read "Member usage could not be loaded."
///
/// Six unit tests over a mocked IUsageRecordRepository passed throughout, because the defect was
/// in the LINQ those tests replaced.
/// </summary>
public sealed class UsageRecordRepositoryUsageByMemberTests : IAsyncLifetime
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

    private readonly Guid _bigSpender = Guid.NewGuid();
    private readonly Guid _smallSpender = Guid.NewGuid();

    private BillingDbContext _context = null!;
    private UsageRecordRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _context = new BillingDbContext(
            new DbContextOptionsBuilder<BillingDbContext>()
                .UseNpgsql(_database.GetConnectionString())
                .Options);

        // The schema declares uuidv7() defaults and postgres:16 has no such builtin — the same
        // shim the other integration tests install before EnsureCreated runs the DDL.
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

        _repository = new UsageRecordRepository(_context);

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
            NewSubscription(_subscriptionId, _workspaceId),
            NewSubscription(_otherSubscriptionId, _otherWorkspaceId));

        _context.UsageRecords.AddRange(
            // The small spender is added FIRST so a passing ordering assertion cannot be
            // insertion order wearing a disguise.
            NewUsage(_subscriptionId, _workspaceId, _smallSpender, 40, Anchor.AddDays(1)),
            NewUsage(_subscriptionId, _workspaceId, _bigSpender, 100, Anchor.AddDays(1)),
            NewUsage(_subscriptionId, _workspaceId, _bigSpender, 60, Anchor.AddDays(2)),
            // Another workspace's spend must never reach this workspace's figures.
            NewUsage(_otherSubscriptionId, _otherWorkspaceId, _bigSpender, 999, Anchor.AddDays(1)),
            // Unattributed usage is excluded rather than bucketed — see IUsageRecordRepository.
            NewUsage(_subscriptionId, _workspaceId, null, 7, Anchor.AddDays(1)),
            // Outside every window under test.
            NewUsage(_subscriptionId, _workspaceId, _bigSpender, 500, Anchor.AddDays(40)));

        await _context.SaveChangesAsync();
    }

    private Subscription NewSubscription(Guid id, Guid workspaceId) => new()
    {
        Id = id,
        UserId = _bigSpender,
        WorkspaceId = workspaceId,
        PlanId = _planId,
        Status = "active",
        CreditsRemaining = 640,
        CreditsUsedThisCycle = 360,
        CurrentPeriodStart = Anchor,
        CurrentPeriodEnd = Anchor.AddDays(30),
        IsActive = true,
        CreatedAt = Anchor,
        UpdatedAt = Anchor,
    };

    private static UsageRecord NewUsage(
        Guid subscriptionId, Guid workspaceId, Guid? userId, int credits, DateTime recordedAt) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = subscriptionId,
        WorkspaceId = workspaceId,
        UserId = userId,
        UsageType = "voice_translation",
        Unit = "request",
        Quantity = 1,
        CreditsConsumed = credits,
        RecordedAt = recordedAt,
    };

    [Fact]
    public async Task The_query_translates_to_SQL_at_all()
    {
        // The regression itself. Before the fix this threw InvalidOperationException — "The LINQ
        // expression could not be translated" — and the assertions below never got to run.
        var act = async () => await _repository.GetUsageByMemberAsync(_workspaceId, null, null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Sums_and_counts_each_member_in_this_workspace()
    {
        var rows = await _repository.GetUsageByMemberAsync(_workspaceId, null, null);

        rows.Should().HaveCount(2);

        var big = rows.Single(r => r.UserId == _bigSpender);
        big.CreditsConsumed.Should().Be(660); // 100 + 60 + 500
        big.RecordCount.Should().Be(3);
        big.LastUsedAt.Should().Be(Anchor.AddDays(40));

        var small = rows.Single(r => r.UserId == _smallSpender);
        small.CreditsConsumed.Should().Be(40);
        small.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task Orders_by_credits_consumed_descending()
    {
        // The ORDER BY is the clause that broke the query, so it is asserted rather than assumed.
        var rows = await _repository.GetUsageByMemberAsync(_workspaceId, null, null);

        rows.Select(r => r.UserId).Should().ContainInOrder(_bigSpender, _smallSpender);
    }

    [Fact]
    public async Task Excludes_usage_that_belongs_to_another_workspace()
    {
        var rows = await _repository.GetUsageByMemberAsync(_workspaceId, null, null);

        // The big spender also has 999 credits in the other workspace; none of it is here.
        rows.Single(r => r.UserId == _bigSpender).CreditsConsumed.Should().Be(660);
    }

    [Fact]
    public async Task Excludes_usage_with_no_user_attribution()
    {
        var rows = await _repository.GetUsageByMemberAsync(_workspaceId, null, null);

        // 7 unattributed credits were seeded. A silent "unknown" bucket would hide the day
        // attribution starts failing, so those rows are dropped instead.
        rows.Sum(r => r.CreditsConsumed).Should().Be(700);
    }

    [Fact]
    public async Task Narrows_to_the_window_when_one_is_given()
    {
        // The dashboard always sends `from` and never `to`, which is the shape that reaches
        // production — so the bound that is actually used is the one exercised here.
        var rows = await _repository.GetUsageByMemberAsync(
            _workspaceId, Anchor.AddDays(2), null);

        rows.Should().ContainSingle();
        rows[0].UserId.Should().Be(_bigSpender);
        rows[0].CreditsConsumed.Should().Be(560); // 60 on day 2, 500 on day 40
    }

    [Fact]
    public async Task A_bounded_window_excludes_both_ends_correctly()
    {
        var rows = await _repository.GetUsageByMemberAsync(
            _workspaceId, Anchor.AddDays(1), Anchor.AddDays(2));

        rows.Sum(r => r.CreditsConsumed).Should().Be(200); // 40 + 100 + 60
    }

    [Fact]
    public async Task A_workspace_with_no_usage_is_an_empty_list_rather_than_a_failure()
    {
        var rows = await _repository.GetUsageByMemberAsync(Guid.NewGuid(), null, null);

        rows.Should().BeEmpty();
    }
}
