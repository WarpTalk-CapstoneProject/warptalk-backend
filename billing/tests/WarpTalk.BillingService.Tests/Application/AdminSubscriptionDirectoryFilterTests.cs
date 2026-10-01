using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application;

/// <summary>
/// serviceState, billingCycle, autoRenew, workspaceId, the period-end window and credits_desc on
/// the admin subscription directory: what the service rejects, what the repository's WHERE and
/// ORDER BY keep (over plain rows), and that Npgsql can translate them — all without Docker.
/// AdminSubscriptionDirectoryTests runs them against real PostgreSQL.
/// </summary>
public class AdminSubscriptionDirectoryFilterTests
{
    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly AdminSubscriptionService _service;
    private AdminSubscriptionFilter? _seen;

    public AdminSubscriptionDirectoryFilterTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _subscriptions
            .Setup(r => r.GetAdminDirectoryAsync(
                It.IsAny<AdminSubscriptionFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<AdminSubscriptionFilter, int, int, CancellationToken>((f, _, _, _) => _seen = f)
            .ReturnsAsync(((IReadOnlyList<AdminSubscriptionRow>)new List<AdminSubscriptionRow>(), 0));

        _service = new AdminSubscriptionService(_unitOfWork.Object, Mock.Of<ILogger<AdminSubscriptionService>>());
    }

    // ── Service validation ───────────────────────────────────

    [Theory]
    [InlineData("overdrawn")]
    [InlineData("lowbalance")]
    public async Task An_unknown_service_state_is_a_validation_error(string serviceState)
    {
        var result = await _service.GetDirectoryAsync(new AdminSubscriptionDirectoryQuery { ServiceState = serviceState });

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _seen.Should().BeNull();
    }

    [Theory]
    [InlineData("year")]
    [InlineData("quarterly")]
    public async Task An_unknown_billing_cycle_is_a_validation_error(string cycle)
    {
        var result = await _service.GetDirectoryAsync(new AdminSubscriptionDirectoryQuery { BillingCycle = cycle });

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _seen.Should().BeNull();
    }

    [Fact]
    public async Task An_inverted_period_end_window_is_a_validation_error()
    {
        var result = await _service.GetDirectoryAsync(new AdminSubscriptionDirectoryQuery
        {
            PeriodEndFrom = Anchor.AddDays(5),
            PeriodEndTo = Anchor,
        });

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _seen.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_sort_is_still_a_validation_error()
    {
        var result = await _service.GetDirectoryAsync(new AdminSubscriptionDirectoryQuery { Sort = "revenue_desc" });

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task Every_new_value_reaches_the_repository_normalized()
    {
        var workspaceId = Guid.NewGuid();

        var result = await _service.GetDirectoryAsync(new AdminSubscriptionDirectoryQuery
        {
            ServiceState = " IN_OVERAGE ",
            BillingCycle = "Yearly",
            AutoRenew = false,
            WorkspaceId = workspaceId,
            PeriodEndFrom = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Unspecified),
            PeriodEndTo = Anchor.AddDays(30),
            Sort = "credits_desc",
        });

        result.IsSuccess.Should().BeTrue(result.Error);
        _seen.Should().Be(new AdminSubscriptionFilter(
            Status: null,
            PlanSlug: null,
            Sort: "credits_desc",
            ServiceState: SubscriptionConstants.ServiceStates.InOverage,
            BillingCycle: SubscriptionConstants.BillingCycles.Yearly,
            AutoRenew: false,
            WorkspaceId: workspaceId,
            PeriodEndFrom: Anchor,
            PeriodEndTo: Anchor.AddDays(30)));
        _seen!.PeriodEndFrom!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task All_means_no_filter_for_service_state_and_billing_cycle()
    {
        await _service.GetDirectoryAsync(new AdminSubscriptionDirectoryQuery { ServiceState = "all", BillingCycle = "ALL" });

        _seen!.ServiceState.Should().BeNull();
        _seen.BillingCycle.Should().BeNull();
    }

    [Fact]
    public async Task Defaults_are_unchanged_when_no_new_parameter_is_sent()
    {
        await _service.GetDirectoryAsync(new AdminSubscriptionDirectoryQuery());

        _seen.Should().Be(new AdminSubscriptionFilter());
    }

    // ── Repository WHERE / ORDER BY over plain rows ──────────

    private static readonly Plan Monthly = new() { Id = Guid.NewGuid(), Slug = "pro", BillingCycle = "monthly" };
    private static readonly Plan Yearly = new() { Id = Guid.NewGuid(), Slug = "pro-annual", BillingCycle = "yearly" };
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static Subscription Sub(
        Plan plan,
        string serviceState,
        bool autoRenew,
        DateTime periodEnd,
        int credits,
        Guid? workspaceId = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId ?? Guid.NewGuid(),
        PlanId = plan.Id,
        Plan = plan,
        Status = "active",
        ServiceState = serviceState,
        AutoRenew = autoRenew,
        CurrentPeriodEnd = periodEnd,
        CreditsRemaining = credits,
        CreatedAt = Anchor,
    };

    private static readonly Subscription Healthy =
        Sub(Monthly, SubscriptionConstants.ServiceStates.Healthy, true, Anchor.AddDays(10), 500, WorkspaceA);
    private static readonly Subscription Overage =
        Sub(Yearly, SubscriptionConstants.ServiceStates.InOverage, false, Anchor.AddDays(20), 50);
    private static readonly Subscription Low =
        Sub(Monthly, SubscriptionConstants.ServiceStates.LowBalance, true, Anchor.AddDays(30), 5000);

    private static List<Guid> Filter(AdminSubscriptionFilter filter) =>
        SubscriptionRepository.ApplyAdminFilters(new[] { Healthy, Overage, Low }.AsQueryable(), filter)
            .Select(s => s.Id)
            .ToList();

    [Fact]
    public void Each_new_filter_narrows_the_rows()
    {
        Filter(new AdminSubscriptionFilter(ServiceState: "in_overage")).Should().Equal(Overage.Id);
        Filter(new AdminSubscriptionFilter(BillingCycle: "monthly")).Should().Equal(Healthy.Id, Low.Id);
        Filter(new AdminSubscriptionFilter(AutoRenew: false)).Should().Equal(Overage.Id);
        Filter(new AdminSubscriptionFilter(WorkspaceId: WorkspaceA)).Should().Equal(Healthy.Id);
        Filter(new AdminSubscriptionFilter()).Should().HaveCount(3);
    }

    [Fact]
    public void The_period_end_window_is_inclusive_from_and_exclusive_to()
    {
        Filter(new AdminSubscriptionFilter(PeriodEndFrom: Anchor.AddDays(10), PeriodEndTo: Anchor.AddDays(30)))
            .Should().Equal(Healthy.Id, Overage.Id);
    }

    [Fact]
    public void Credits_desc_puts_the_largest_balance_first()
    {
        SubscriptionRepository.ApplyAdminSort(new[] { Healthy, Overage, Low }.AsQueryable(), "credits_desc")
            .Select(s => s.Id)
            .Should().Equal(Low.Id, Healthy.Id, Overage.Id);
    }

    // ── Translation ──────────────────────────────────────────

    [Fact]
    public void The_new_filters_and_sort_translate_to_sql()
    {
        using var context = new BillingDbContext(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);

        var filter = new AdminSubscriptionFilter(
            ServiceState: "healthy",
            BillingCycle: "yearly",
            AutoRenew: true,
            WorkspaceId: Guid.NewGuid(),
            PeriodEndFrom: Anchor,
            PeriodEndTo: Anchor.AddDays(30));

        var sql = SubscriptionRepository
            .ApplyAdminSort(SubscriptionRepository.ApplyAdminFilters(context.Subscriptions, filter), "credits_desc")
            .ToQueryString();

        sql.Should().Contain("service_state = @")
            .And.Contain("billing_cycle = @")
            .And.Contain("auto_renew = @")
            .And.Contain("workspace_id = @")
            .And.Contain("current_period_end >= @")
            .And.Contain("current_period_end < @")
            .And.MatchRegex(@"ORDER BY \w+\.credits_remaining DESC");
    }
}
