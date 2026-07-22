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

public record TopUpRequest(
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
);

public record AdjustCreditsRequest(
    [Required]
    int Amount,
    [Required]
    string Reason
);

public record UsageAlertDto(
    Guid WorkspaceId,
    string WorkspaceName,
    int ConsumedCreditsIn24h,
    string Reason
);
