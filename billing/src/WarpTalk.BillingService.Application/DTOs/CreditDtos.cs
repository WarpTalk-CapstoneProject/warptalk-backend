using System;
using System.ComponentModel.DataAnnotations;
using WarpTalk.Shared;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.DTOs;


public record CreditBalanceDto(
    Guid WorkspaceId,
    int CurrentCredits,
    int CreditsUsedThisCycle,
    int TotalCredits,
    string Status,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd
);

/// <summary>
/// Credits a workspace kept from a subscription that ended (CreditFreezeService). Shown on the
/// billing page as "X credits kept — renew to use them". Zero when nothing is frozen.
/// </summary>
/// <param name="FrozenCredits">Kept and not spendable until the workspace renews.</param>
/// <param name="FrozenAt">When the ended subscription's balance was frozen.</param>
/// <param name="EndedAt">When that subscription stopped being usable.</param>
/// <param name="DormantSince">Set once the grace window passed without a renewal. Still kept.</param>
/// <param name="GraceEndsAt">When frozen credits become dormant, if they are not dormant yet.</param>
/// <param name="HasActiveSubscription">Whether the workspace has a live plan right now.</param>
public record FrozenCreditsDto(
    Guid WorkspaceId,
    int FrozenCredits,
    DateTime? FrozenAt,
    DateTime? EndedAt,
    DateTime? DormantSince,
    DateTime? GraceEndsAt,
    bool HasActiveSubscription,
    /// <summary>
    /// The plan of the workspace's most recent subscription when it has no live one — so the
    /// renew screen can say "the X plan ended on …" even when nothing was left to freeze.
    /// Null when there is a live subscription, or there never was one.
    /// </summary>
    string? LastPlanName = null,
    DateTime? LastEndedAt = null);

public record ConsumeCreditsRequest(
    [Required]
    Guid WorkspaceId,

    [Required]
    [Range(1, int.MaxValue, ErrorMessage = WarpTalk.Shared.ApiMessageConstants.ValidationMessages.AmountGreaterThanZero)]
    int Amount,

    [Required(ErrorMessage = WarpTalk.Shared.ApiMessageConstants.ValidationMessages.ReferenceTypeRequired)]
    string ReferenceType,

    Guid? ReferenceId,

    string? IdempotencyKey = null
) : IWorkspaceScopedRequest;



/// <summary>
/// A manual, admin-only credit adjustment. Keyed by WORKSPACE in the route rather than by
/// subscription id: the admin portal knows workspaces, and which subscription currently holds the
/// balance is this service's business — the resolution happens once, server-side, instead of every
/// caller guessing.
/// </summary>
public record AdjustCreditsRequest(
    [Required]
    [Range(int.MinValue, int.MaxValue)]
    int Amount,

    [Required]
    string Reason
);

public record CreditTransactionDto(
    Guid Id,
    int Amount,        // negative = consumption, positive = top-up
    string Type,          // Use TransactionConstants.TransactionTypes ("consume" | "top_up" | "adjustment" | "refund")
    string? Description,
    string? ReferenceType, // Use TransactionConstants.ReferenceTypes
    Guid? ReferenceId,
    int BalanceAfter,
    DateTime CreatedAt,
    Guid? WorkspaceId = null,
    string? WorkspaceName = null,
    Guid? UserId = null,
    string? UserName = null
)
{
    public CreditTransactionDto(
        Guid id,
        Guid workspaceId,
        int amount,
        string type,
        Guid? referenceId,
        string? referenceType,
        DateTime createdAt)
        : this(id, amount, type, null, referenceType, referenceId, 0, createdAt, workspaceId)
    {
    }
}



public record UsageAlertDto(
    Guid WorkspaceId,
    string WorkspaceName,
    int ConsumedCreditsIn24h,
    string Reason
);

public record StripeSubscriptionTransactionRequest(
    Domain.Entities.Subscription Subscription,
    Domain.Entities.Plan Plan,
    string PaymentType,
    Guid UserId,
    Guid ReferenceId
);
