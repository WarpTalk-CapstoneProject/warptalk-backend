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
public sealed record AdminSubscriptionFilter(
    string? Status = null,
    string? PlanSlug = null,
    string Sort = "period_end_asc");

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

    Task DeactivateOtherActiveSubscriptionsAsync(Guid userId, Guid excludeSubscriptionId, CancellationToken cancellationToken);
    Task<PagedResult<Subscription>> GetPageAsync(PageRequest page, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Subscription>> GetActiveSubscriptionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Subscription>> GetDueForRenewalAsync(DateTime renewalThreshold, DateTime lowerBound, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Subscription>> GetExpiredActiveSubscriptionsAsync(DateTime now, CancellationToken cancellationToken = default);
    Task<Subscription?> GetActiveByWorkspaceIdAsync(Guid workspaceId, bool includePlan = true, bool requireActivePeriod = false, CancellationToken cancellationToken = default);
}
