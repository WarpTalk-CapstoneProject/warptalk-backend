using System;
using System.Linq;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Services;

/// <summary>
/// WT-878: which of a workspace's subscription rows is "its subscription".
///
/// A workspace that re-subscribed after expiry, or changed plan, keeps its old rows (is_active =
/// false) beside the new one. Picking with an unordered FirstOrDefault let the database choose, and
/// a dead row meant hasActiveSubscription = false — the platform floor (5 rooms, 2 participants,
/// voice clone off) — while the billing page showed an active plan.
///
/// The order, most preferred first:
///   1. is_active — the live row;
///   2. status = active — among live rows, one not cancelled-at-period-end;
///   3. newest current_period_start, then newest created_at, then id — deterministic.
/// Plain IQueryable expressions, so the repository runs them in SQL and tests run them over lists.
/// </summary>
public static class SubscriptionSelection
{
    public static IQueryable<Subscription> ForWorkspace(IQueryable<Subscription> query, Guid workspaceId) =>
        query.Where(s => s.WorkspaceId == workspaceId && s.DeletedAt == null);

    public static IOrderedQueryable<Subscription> OrderCurrentFirst(IQueryable<Subscription> query) =>
        query
            .OrderByDescending(s => s.IsActive)
            .ThenByDescending(s => s.Status == SubscriptionConstants.SubscriptionStatuses.Active)
            .ThenByDescending(s => s.CurrentPeriodStart)
            .ThenByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id);
}
