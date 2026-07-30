using System;
using System.ComponentModel.DataAnnotations;

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
    [Range(1, int.MaxValue, ErrorMessage = "Amount must be at least 1.")]
    int Amount,

    [Required(ErrorMessage = "ReferenceType is required.")]
    [MaxLength(100)]
    string ReferenceType,

    Guid? ReferenceId
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
    [property: Required]
    [property: Range(-1_000_000, 1_000_000)]
    int Amount,
    [property: Required]
    [property: MaxLength(500)]
    string Reason
);

public record UsageAlertDto(
    Guid WorkspaceId,
    string WorkspaceName,
    int ConsumedCreditsIn24h,
    string Reason
);
