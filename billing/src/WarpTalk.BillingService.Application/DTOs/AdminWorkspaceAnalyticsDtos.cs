using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Current credit position for a workspace (WT-206).
/// </summary>
/// <param name="SubscriptionFound">
/// False when the workspace has no billing subscription at all. The nullable figures below stay
/// null in that case, so "not set up for billing" reads differently from "set up with zero
/// credits" — the acceptance criterion about distinguishing empty from unavailable.
/// </param>
public record AdminWorkspaceCreditSummaryDto(
    bool SubscriptionFound,
    int? CreditsRemaining,
    int? CreditsUsedThisCycle,
    DateTime? CurrentPeriodStart,
    DateTime? CurrentPeriodEnd,
    Guid? PlanId);

public record AdminWorkspaceUsagePointDto(
    DateTime Date,
    int CreditsConsumed,
    int Events);

public record AdminWorkspaceFeatureUsageDto(
    string UsageType,
    int CreditsConsumed,
    decimal Quantity,
    int Events);

/// <param name="MeetingsWithBillableUsage">
/// Distinct translation rooms that produced a usage record in the window. This is a billing
/// figure, not a meeting count: a meeting that consumed no credits does not appear, because the
/// billing service cannot see meetings it never billed.
/// </param>
public record AdminWorkspaceAnalyticsDto(
    Guid WorkspaceId,
    DateTime From,
    DateTime To,
    AdminWorkspaceCreditSummaryDto Credits,
    int CreditsConsumedInPeriod,
    int CreditsToppedUpInPeriod,
    int MeetingsWithBillableUsage,
    int DistinctUsersBilled,
    IReadOnlyList<AdminWorkspaceUsagePointDto> ConsumptionSeries,
    IReadOnlyList<AdminWorkspaceFeatureUsageDto> FeatureBreakdown);

/// <param name="Amount">
/// Signed: negative for consumption, positive for top-ups and credits back. Taken straight from
/// the ledger rather than re-derived from the type, so the sign always matches what was booked.
/// </param>
public record AdminCreditTransactionDto(
    Guid Id,
    DateTime CreatedAt,
    string Type,
    string? Description,
    Guid? ReferenceId,
    string? ReferenceType,
    int Amount,
    int BalanceAfter,
    string? Currency,
    string Status);

public record AdminCreditTransactionQuery : AdminPageRequest
{
    /// <summary>consume | topup | refund | reserve | adjustment — matched case-insensitively.</summary>
    public string? Type { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    /// <summary>Filter to the transactions raised against one meeting, invoice, or payment.</summary>
    public Guid? ReferenceId { get; init; }

    public int? MinAmount { get; init; }

    public int? MaxAmount { get; init; }
}
