using System;
using FluentAssertions;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;

namespace WarpTalk.BillingService.Tests.Domain.Entities;

/// <summary>
/// WT-430. The exact production state that cost two days of free-tier quotas under an Enterprise
/// label: <c>is_active = true</c>, a period ending three weeks out, and <c>status = 'cancelled'</c>.
/// Two of the three conditions passed. Any test that checked only two would have called it live.
/// </summary>
public class SubscriptionLivenessTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

    private static Subscription Sub(
        string status = SubscriptionConstants.SubscriptionStatuses.Active,
        bool isActive = true,
        int periodEndsInDays = 30) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        Status = status,
        IsActive = isActive,
        CurrentPeriodStart = Now.AddDays(-1),
        CurrentPeriodEnd = Now.AddDays(periodEndsInDays),
    };

    [Fact]
    public void AllThreeConditionsMet_GrantsThePlan()
    {
        Sub().GrantsPlanEntitlements(Now).Should().BeTrue();
    }

    [Fact]
    public void CancelledButStillInPeriodAndFlaggedActive_DoesNotGrantThePlan()
    {
        // The production row, reproduced. is_active said yes, the period said yes, status said no —
        // and status is the one that decides, because a cancelled plan is not a plan in force.
        var subscription = Sub(status: SubscriptionConstants.SubscriptionStatuses.Cancelled);

        subscription.IsActive.Should().BeTrue();
        subscription.CurrentPeriodEnd.Should().BeAfter(Now);
        subscription.GrantsPlanEntitlements(Now).Should().BeFalse();
    }

    [Fact]
    public void ActiveStatusButDeactivatedRow_DoesNotGrantThePlan()
    {
        Sub(isActive: false).GrantsPlanEntitlements(Now).Should().BeFalse();
    }

    [Fact]
    public void ActiveButThePeriodHasPassed_DoesNotGrantThePlan()
    {
        Sub(periodEndsInDays: -1).GrantsPlanEntitlements(Now).Should().BeFalse();
    }

    [Fact]
    public void ThePeriodBoundaryIsInclusive()
    {
        // A subscription is live on its last day, not live the instant after.
        var subscription = Sub(periodEndsInDays: 0);

        subscription.GrantsPlanEntitlements(subscription.CurrentPeriodEnd).Should().BeTrue();
        subscription.GrantsPlanEntitlements(subscription.CurrentPeriodEnd.AddTicks(1)).Should().BeFalse();
    }

    [Theory]
    [InlineData(SubscriptionConstants.SubscriptionStatuses.Pending)]
    [InlineData(SubscriptionConstants.SubscriptionStatuses.Expired)]
    [InlineData(SubscriptionConstants.SubscriptionStatuses.Suspended)]
    [InlineData(SubscriptionConstants.SubscriptionStatuses.None)]
    public void OnlyTheActiveStatusGrantsThePlan(string status)
    {
        Sub(status: status).GrantsPlanEntitlements(Now).Should().BeFalse();
    }
}
