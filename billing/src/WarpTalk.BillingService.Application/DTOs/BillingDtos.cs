using System;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.DTOs;

public record PagedResult<T>(
    int TotalCount,
    IEnumerable<T> Items
);


public record SubscriptionDto(
    Guid   Id,
    Guid?  WorkspaceId,
    Guid   PlanId,
    string PlanName,
    string Status,
    int    CreditsRemaining,
    int    CreditsUsedThisCycle,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd,
    bool   AutoRenew,
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
