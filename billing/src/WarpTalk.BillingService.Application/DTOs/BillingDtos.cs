using System;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.DTOs;

public record PagedResult<T>(
    int TotalCount,
    IEnumerable<T> Items
);

public record BillingReportDto(
    Guid WorkspaceId,
    int Month,
    int Year,
    int StartingBalance,
    int EndingBalance,
    int TotalTopUpCredits,
    int TotalConsumedCredits,
    IEnumerable<UsageSummaryDto> UsageBreakdown
);

public record UsageSummaryDto(
    string UsageType,
    int TotalCreditsConsumed
);


public record SubscriptionDto(
    Guid Id,
    Guid UserId,
    Guid? WorkspaceId,
    Guid PlanId,
    string PlanName,
    decimal Price,
    string Status,
    int CreditsRemaining,
    int CreditsUsedThisCycle,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd,
    bool AutoRenew,
    bool CancelAtPeriodEnd,
    DateTime CreatedAt,
    DateTime? CancelledAt
);

public record CreateSubscriptionRequest(
    [Required(ErrorMessage = "WorkspaceId is required.")]
    Guid WorkspaceId,

    [Required(ErrorMessage = "PlanId is required.")]
    Guid PlanId,

    [Required(ErrorMessage = "UserId is required.")]
    Guid UserId
);

public record CancelSubscriptionRequest(
    [MaxLength(500, ErrorMessage = "Reason cannot exceed 500 characters.")]
    string? Reason
);

public record ChangeSubscriptionRequest(
    [Required(ErrorMessage = "WorkspaceId is required.")]
    Guid WorkspaceId,

    [Required(ErrorMessage = "NewPlanId is required.")]
    Guid NewPlanId
);

public record RecordUsageRequest(
    [Required(ErrorMessage = "HostWorkspaceId is required.")]
    Guid HostWorkspaceId,

    [Required(ErrorMessage = "UserId is required.")]
    Guid UserId,

    [Required(ErrorMessage = "UsageType is required.")]
    string UsageType,

    [Required(ErrorMessage = "Unit is required.")]
    string Unit,

    decimal Quantity,
    int CreditsConsumed,
    int? DurationSeconds,
    Guid? TranslationRoomId,
    string? Details
);
