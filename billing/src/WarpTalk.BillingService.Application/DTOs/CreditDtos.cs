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

public record TopUpRequest(
    [Required]
    Guid WorkspaceId,

    [Required]
    [Range(1, int.MaxValue, ErrorMessage = WarpTalk.Shared.ApiMessageConstants.ValidationMessages.AmountGreaterThanZero)]
    int Amount,

    [Required(ErrorMessage = WarpTalk.Shared.ApiMessageConstants.ValidationMessages.ReferenceTypeRequired)]
    string ReferenceType,

    Guid? ReferenceId
) : IWorkspaceScopedRequest;

public record GrantCreditsRequest(
    Guid WorkspaceId,
    int Amount,
    string ReferenceType,
    Guid? ReferenceId,
    Guid? UserId,
    string? Description = null
) : IWorkspaceScopedRequest;

public record SimulatePaymentRequest(
    [Required]
    Guid WorkspaceId,
    decimal Amount = 10m,
    string Currency = PaymentConstants.Currencies.Usd
) : IWorkspaceScopedRequest;


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

public record ManualAdjustCreditsRequest(
    [Required]
    Guid WorkspaceId,

    [Required]
    int Amount,

    [Required]
    string Reason,

    [Required]
    string AdminUserId
) : IWorkspaceScopedRequest;

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

public record CreditCostRequest(
    int AudioSeconds,
    int TokenCount,
    int GpuInferenceMs,
    bool IsVoiceClone,
    ServiceRatesDto Rates
);
