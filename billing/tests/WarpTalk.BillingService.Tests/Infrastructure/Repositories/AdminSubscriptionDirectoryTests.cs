using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.BillingService.Tests.Infrastructure.Repositories;

/// <summary>
/// Real PostgreSQL, for the reason this service learned the hard way: usage-by-member was covered
/// only by a mocked repository, the mock replaced the exact LINQ that was broken, and the endpoint
/// returned 500 on every call it ever served.
///
/// The directory query is the same shape — filter, join to plan, sort, page — so the first thing
/// asserted is simply that it runs.
/// </summary>
public sealed class AdminSubscriptionDirectoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _monthlyPlanId = Guid.NewGuid();
    private readonly Guid _yearlyPlanId = Guid.NewGuid();
    private readonly Guid _activeId = Guid.NewGuid();
    private readonly Guid _trialId = Guid.NewGuid();
    private readonly Guid _cancelledId = Guid.NewGuid();
    private readonly Guid _deletedId = Guid.NewGuid();

    private BillingDbContext _context = null!;
    private SubscriptionRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _context = new BillingDbContext(
            new DbContextOptionsBuilder<BillingDbContext>()
                .UseNpgsql(_database.GetConnectionString())
                .Options);

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

        _repository = new SubscriptionRepository(_context);
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        _context.Plans.AddRange(
            NewPlan(_monthlyPlanId, "pro", "monthly", 500_000m, "VND"),
            NewPlan(_yearlyPlanId, "pro-annual", "yearly", 6_000_000m, "VND"));

        _context.Subscriptions.AddRange(
            NewSubscription(_activeId, _monthlyPlanId, "active", periodEnd: Anchor.AddDays(40)),
            NewSubscription(_trialId, _yearlyPlanId, "active", periodEnd: Anchor.AddDays(10),
                trialEndsAt: DateTime.UtcNow.AddDays(7)),
            NewSubscription(_cancelledId, _monthlyPlanId, "cancelled", periodEnd: Anchor.AddDays(5),
                cancelledAt: Anchor.AddDays(1)),
            NewSubscription(_deletedId, _monthlyPlanId, "active", periodEnd: Anchor.AddDays(20),
                deletedAt: Anchor.AddDays(2)));

        await _context.SaveChangesAsync();
    }

    private static Plan NewPlan(Guid id, string slug, string cycle, decimal price, string currency) => new()
    {
        Id = id,
        Name = slug,
        Slug = slug,
        Tier = "pro",
        Price = price,
        Currency = currency,
        BillingCycle = cycle,
        CreditsPerCycle = 1000,
        IsActive = true,
        CreatedAt = Anchor,
        UpdatedAt = Anchor,
    };

    private static Subscription NewSubscription(
        Guid id,
        Guid planId,
        string status,
        DateTime periodEnd,
        DateTime? trialEndsAt = null,
        DateTime? cancelledAt = null,
        DateTime? deletedAt = null) => new()
    {
        Id = id,
        UserId = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        PlanId = planId,
        Status = status,
        CreditsRemaining = 500,
        CreditsUsedThisCycle = 100,
        CurrentPeriodStart = Anchor,
        CurrentPeriodEnd = periodEnd,
        AutoRenew = true,
        TrialEndsAt = trialEndsAt,
        CancelledAt = cancelledAt,
        DeletedAt = deletedAt,
        IsActive = true,
        ServiceState = SubscriptionConstants.ServiceStates.Healthy,
        CreatedAt = Anchor,
        UpdatedAt = Anchor,
    };

    [Fact]
    public async Task The_directory_query_translates_to_SQL_at_all()
    {
        var act = async () =>
            await _repository.GetAdminDirectoryAsync(new AdminSubscriptionFilter(), 1, 20);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_revenue_query_translates_to_SQL_at_all()
    {
        var act = async () => await _repository.GetActiveForRevenueAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Soft_deleted_subscriptions_are_never_listed()
    {
        var (items, total) = await _repository.GetAdminDirectoryAsync(new AdminSubscriptionFilter(), 1, 20);

        total.Should().Be(3);
        items.Should().NotContain(s => s.Id == _deletedId);
    }

    [Fact]
    public async Task The_plan_is_joined_so_price_currency_and_cycle_come_back()
    {
        var (items, _) = await _repository.GetAdminDirectoryAsync(new AdminSubscriptionFilter(), 1, 20);

        var yearly = items.Single(s => s.Id == _trialId);
        yearly.BillingCycle.Should().Be("yearly");
        yearly.PlanPrice.Should().Be(6_000_000m);
        yearly.PlanCurrency.Should().Be("VND");
        yearly.PlanSlug.Should().Be("pro-annual");
    }

    [Fact]
    public async Task Filtering_by_status_narrows_the_total_not_just_the_page()
    {
        var (items, total) = await _repository.GetAdminDirectoryAsync(
            new AdminSubscriptionFilter(Status: "cancelled"), 1, 20);

        total.Should().Be(1);
        items.Single().Id.Should().Be(_cancelledId);
    }

    [Fact]
    public async Task Filtering_by_plan_slug_uses_the_joined_plan()
    {
        var (_, total) = await _repository.GetAdminDirectoryAsync(
            new AdminSubscriptionFilter(PlanSlug: "pro-annual"), 1, 20);

        total.Should().Be(1);
    }

    [Fact]
    public async Task The_default_sort_puts_the_soonest_period_end_first()
    {
        // The screen answers "what needs attention", and what needs attention is what runs out
        // next — so this ordering is asserted rather than assumed.
        var (items, _) = await _repository.GetAdminDirectoryAsync(new AdminSubscriptionFilter(), 1, 20);

        items.Select(s => s.Id).Should().ContainInOrder(_cancelledId, _trialId, _activeId);
    }

    [Fact]
    public async Task The_revenue_query_returns_only_active_undeleted_rows()
    {
        var rows = await _repository.GetActiveForRevenueAsync();

        rows.Should().HaveCount(2);
        rows.Select(r => r.Id).Should().BeEquivalentTo(new[] { _activeId, _trialId });
    }

    [Fact]
    public async Task Paging_reports_the_filtered_total()
    {
        var (items, total) = await _repository.GetAdminDirectoryAsync(new AdminSubscriptionFilter(), 1, 2);

        items.Should().HaveCount(2);
        total.Should().Be(3);
    }
}
