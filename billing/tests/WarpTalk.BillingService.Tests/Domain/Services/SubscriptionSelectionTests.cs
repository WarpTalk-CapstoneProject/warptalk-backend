using System;
using System.Linq;
using FluentAssertions;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Services;
using Xunit;

namespace WarpTalk.BillingService.Tests.Domain.Services;

/// <summary>WT-878 (bug A): which of a workspace's rows is "its subscription".</summary>
public class SubscriptionSelectionTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid WorkspaceId = Guid.NewGuid();

    private static Subscription Row(bool isActive, string status, DateTime periodStart, DateTime? deletedAt = null, Guid? workspaceId = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId ?? WorkspaceId,
        IsActive = isActive,
        Status = status,
        CurrentPeriodStart = periodStart,
        CurrentPeriodEnd = periodStart.AddMonths(1),
        CreatedAt = periodStart,
        DeletedAt = deletedAt
    };

    private static Subscription? Pick(params Subscription[] rows) =>
        SubscriptionSelection.OrderCurrentFirst(SubscriptionSelection.ForWorkspace(rows.AsQueryable(), WorkspaceId)).FirstOrDefault();

    [Fact]
    public void The_active_row_wins_even_when_an_inactive_row_is_newer()
    {
        var live = Row(true, SubscriptionConstants.SubscriptionStatuses.Active, Now.AddMonths(-2));
        var newerDead = Row(false, SubscriptionConstants.SubscriptionStatuses.Expired, Now.AddDays(-1));

        Pick(live, newerDead).Should().BeSameAs(live);
        Pick(newerDead, live).Should().BeSameAs(live);
    }

    [Fact]
    public void Among_active_rows_the_active_status_then_the_newest_period_wins()
    {
        var cancelling = Row(true, SubscriptionConstants.SubscriptionStatuses.Cancelled, Now.AddDays(-1));
        var olderActive = Row(true, SubscriptionConstants.SubscriptionStatuses.Active, Now.AddMonths(-2));
        var newerActive = Row(true, SubscriptionConstants.SubscriptionStatuses.Active, Now.AddDays(-3));

        Pick(cancelling, olderActive, newerActive).Should().BeSameAs(newerActive);
        Pick(newerActive, olderActive, cancelling).Should().BeSameAs(newerActive);
    }

    [Fact]
    public void Without_an_active_row_the_newest_inactive_one_is_returned()
    {
        var older = Row(false, SubscriptionConstants.SubscriptionStatuses.Expired, Now.AddMonths(-4));
        var newer = Row(false, SubscriptionConstants.SubscriptionStatuses.Cancelled, Now.AddMonths(-1));

        Pick(older, newer).Should().BeSameAs(newer);
        Pick(newer, older).Should().BeSameAs(newer);
    }

    [Fact]
    public void Soft_deleted_rows_and_other_workspaces_are_never_returned()
    {
        var deleted = Row(true, SubscriptionConstants.SubscriptionStatuses.Active, Now, deletedAt: Now);
        var otherWorkspace = Row(true, SubscriptionConstants.SubscriptionStatuses.Active, Now, workspaceId: Guid.NewGuid());

        Pick(deleted, otherWorkspace).Should().BeNull();
    }
}
