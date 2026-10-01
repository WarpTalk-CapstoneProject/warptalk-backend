using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>Query string contract for the platform subscription directory. Bound with [FromQuery].</summary>
public record AdminSubscriptionDirectoryQuery : AdminPageRequest
{
    /// <summary>One of the subscription statuses, or null for every status.</summary>
    public string? Status { get; init; }

    /// <summary>A plan slug. Null lists every plan.</summary>
    public string? PlanSlug { get; init; }

    /// <summary>
    /// healthy | low_balance | in_overage | suspended (the subscription's service_state), or
    /// all / null for every state. Anything else is a 400.
    /// </summary>
    public string? ServiceState { get; init; }

    /// <summary>
    /// The PLAN's billing cycle: monthly | yearly, or all / null for both. Anything else is a 400.
    /// </summary>
    public string? BillingCycle { get; init; }

    /// <summary>true or false to match subscriptions.auto_renew; null for both.</summary>
    public bool? AutoRenew { get; init; }

    /// <summary>Only the subscriptions of this workspace.</summary>
    public Guid? WorkspaceId { get; init; }

    /// <summary>
    /// Inclusive lower bound on <c>current_period_end</c> (UTC; a value without an offset is read
    /// as UTC). Must not be later than <see cref="PeriodEndTo"/>.
    /// </summary>
    public DateTime? PeriodEndFrom { get; init; }

    /// <summary>Exclusive upper bound on <c>current_period_end</c> (UTC).</summary>
    public DateTime? PeriodEndTo { get; init; }

    /// <summary>
    /// period_end_asc | period_end_desc | created_desc | created_asc | credits_asc | credits_desc.
    /// Defaults to period_end_asc — soonest renewal first, because that is what needs attention.
    /// </summary>
    public string? Sort { get; init; }
}

/// <summary>
/// One subscription in the directory.
///
/// <paramref name="MonthlyValue"/> is this subscription's own contribution to recurring revenue,
/// already resolved: contract price over plan price, yearly divided by twelve, in the currency it
/// is actually denominated in. It is null while the subscription is not recurring — a trial, or a
/// cancelled row — because zero and "not applicable" are different answers.
/// </summary>
public record AdminSubscriptionSummaryDto(
    Guid Id,
    Guid WorkspaceId,
    string Status,
    string ServiceState,
    string? SuspendedReason,
    string PlanName,
    string PlanSlug,
    string PlanTier,
    string BillingCycle,
    AdminMoney? MonthlyValue,
    int CreditsRemaining,
    int CreditsUsedThisCycle,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd,
    bool AutoRenew,
    /// <summary>Set while the subscription is still inside its trial window.</summary>
    DateTime? TrialEndsAt,
    DateTime? CancelledAt,
    DateTime CreatedAt);

/// <summary>
/// The revenue headline.
///
/// <paramref name="MonthlyRecurring"/> is a LIST, one entry per currency, and never a single
/// number: the platform prices in VND and in USD, and the only exchange rate available is a seed
/// constant nobody maintains. A split figure is legible; a converted one is confidently wrong.
/// </summary>
public record AdminSubscriptionSummaryTotalsDto(
    IReadOnlyList<AdminMoney> MonthlyRecurring,
    int ActiveCount,
    int TrialCount,
    int PastDueCount,
    int CancelledCount,
    /// <summary>Active subscriptions whose current period ends within 14 days, renewing or not.</summary>
    int EndingWithin14Days);
