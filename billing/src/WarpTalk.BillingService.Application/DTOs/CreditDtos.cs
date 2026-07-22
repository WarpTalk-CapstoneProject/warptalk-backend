using WarpTalk.Shared;
using System;
using System.ComponentModel.DataAnnotations;
using WarpTalk.BillingService.Domain.Enums;

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
    CreditReferenceType ReferenceType,

    Guid? ReferenceId
);

public record TopUpRequest(
    [Required]
    Guid WorkspaceId,

    [Required]
    [Range(1, int.MaxValue, ErrorMessage = WarpTalk.Shared.ApiMessageConstants.ValidationMessages.AmountGreaterThanZero)]
    int Amount,

    [Required(ErrorMessage = WarpTalk.Shared.ApiMessageConstants.ValidationMessages.ReferenceTypeRequired)]
    CreditReferenceType ReferenceType,

    Guid? ReferenceId
);

public record SimulatePaymentRequest(
    [Required]
    Guid WorkspaceId,
    decimal Amount = 190000m,
    string Currency = "vnd"
);


public record CreditTransactionDto(
    Guid Id,
    int Amount,        // negative = consumption, positive = top-up
    string Type,          // "consumption" | "top_up"
    string? Description,
    string? ReferenceType,
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

public record AdjustCreditsRequest(
    [Required]
    Guid WorkspaceId,

    [Required]
    int Amount,

    [Required]
    string Reason,

    [Required]
    string AdminUserId
);

public record UsageAlertDto(
    Guid WorkspaceId,
    string WorkspaceName,
    int ConsumedCreditsIn24h,
    string Reason
);
