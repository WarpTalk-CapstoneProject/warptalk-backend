using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>What the platform-admin subscription directory is being asked for.</summary>
/// <param name="Status">
/// One of SubscriptionConstants.SubscriptionStatuses, or null for every status. Validated before
/// it reaches here.
/// </param>
/// <param name="PlanSlug">A plan slug. Null lists every plan.</param>
/// <param name="ServiceState">One of SubscriptionConstants.ServiceStates, or null. Validated before it reaches here.</param>
/// <param name="BillingCycle">The plan's cycle, one of SubscriptionConstants.BillingCycles, or null.</param>
/// <param name="AutoRenew">Matches subscriptions.auto_renew when set.</param>
/// <param name="WorkspaceId">Only this workspace's subscriptions when set.</param>
/// <param name="PeriodEndFrom">Inclusive lower bound on current_period_end, already UTC.</param>
/// <param name="PeriodEndTo">Exclusive upper bound on current_period_end, already UTC.</param>
public sealed record AdminSubscriptionFilter(
    string? Status = null,
    string? PlanSlug = null,
    string Sort = "period_end_asc",
    string? ServiceState = null,
    string? BillingCycle = null,
    bool? AutoRenew = null,
    Guid? WorkspaceId = null,
    DateTime? PeriodEndFrom = null,
    DateTime? PeriodEndTo = null);

/// <summary>
/// One subscription as the directory lists it, already joined to its plan.
///
/// <paramref name="ContractPriceVnd"/> is carried separately from <paramref name="PlanPrice"/>
/// rather than resolved here: which one applies is a commercial rule, and the money layer above
/// is where it is decided and tested.
/// </summary>
public sealed record AdminSubscriptionRow(
    Guid Id,
    Guid WorkspaceId,
    string Status,
    string ServiceState,
    string? SuspendedReason,
    string PlanName,
    string PlanSlug,
    string PlanTier,
    string BillingCycle,
    decimal PlanPrice,
    string PlanCurrency,
    decimal? ContractPriceVnd,
    int CreditsRemaining,
    int CreditsUsedThisCycle,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd,
    bool AutoRenew,
    DateTime? TrialEndsAt,
    DateTime? CancelledAt,
    DateTime CreatedAt);

public interface ISubscriptionRepository : IGenericRepository<Subscription>
{
    /// <summary>
    /// One page of the platform subscription directory, plus the total the filter matches.
    ///
    /// Soft-deleted rows are always excluded: a deleted subscription is not a state an
    /// administrator can act on, and including it would make every count disagree with billing.
    /// </summary>
    Task<(IReadOnlyList<AdminSubscriptionRow> Items, int Total)> GetAdminDirectoryAsync(
        AdminSubscriptionFilter filter,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Every ACTIVE subscription, joined to its plan, for the revenue summary.
    ///
    /// Returns rows rather than a computed total on purpose. Recurring revenue depends on the
    /// billing cycle, on whether a contract price overrides the plan's, and on the currency each
    /// one is denominated in — none of which is the repository's business to decide.
    /// </summary>
    Task<IReadOnlyList<AdminSubscriptionRow>> GetActiveForRevenueAsync(CancellationToken ct = default);

    /// <summary>
    /// Admin Insights: paying-subscription flow over [from, to). See <see cref="SubscriptionFlowCounts"/>
    /// for every definition. <paramref name="now"/> caps cancellations so a cancel-at-period-end with a
    /// future period end is not counted before it happens.
    /// </summary>
    Task<SubscriptionFlowCounts> GetSubscriptionFlowCountsAsync(DateTime from, DateTime to, DateTime now, CancellationToken ct = default);

    /// <summary>Admin Insights: live subscriptions whose period ends in (now, until], soonest first.</summary>
    Task<IReadOnlyList<EndingSoonSubscriptionRow>> GetEndingSoonAsync(DateTime now, DateTime until, int take, CancellationToken ct = default);

    /// <summary>
    /// G12 inbox: live subscriptions whose trial ends in (now, until], whose period ends in (now, until] with
    /// auto-renew off, or whose service is suspended.
    /// </summary>
    Task<IReadOnlyList<InboxSubscriptionRow>> GetNeedingAttentionAsync(DateTime now, DateTime until, int take, CancellationToken ct = default);

    Task DeactivateOtherActiveSubscriptionsAsync(Guid userId, Guid excludeSubscriptionId, CancellationToken cancellationToken);
    Task<PagedResult<Subscription>> GetPageAsync(PageRequest page, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Subscription>> GetActiveSubscriptionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Subscription>> GetDueForRenewalAsync(DateTime renewalThreshold, DateTime lowerBound, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Subscription>> GetExpiredActiveSubscriptionsAsync(DateTime now, CancellationToken cancellationToken = default);
    Task<Subscription?> GetActiveByWorkspaceIdAsync(Guid workspaceId, bool includePlan = true, bool requireActivePeriod = false, CancellationToken cancellationToken = default);
}
