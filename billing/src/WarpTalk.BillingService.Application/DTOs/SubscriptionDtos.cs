using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.DTOs;

// ============================================================================
// RESPONSE DTOs
// ============================================================================

public record SubscriptionDto(
    Guid Id,
    Guid? UserId,
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
    DateTime? CancelledAt,
    string? WorkspaceName = null
)
{
    public int CurrentCredits => CreditsRemaining;
    public DateTime StartDate => CurrentPeriodStart;
    public DateTime? EndDate => CurrentPeriodEnd;
}

public record WorkspaceCreditsDto(
    Guid WorkspaceId,
    int CurrentCredits,
    DateTime? SubscriptionEndDate,
    string SubscriptionStatus = "active");

public record TransactionDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? SubscriptionId,
    decimal Amount,
    string Status,
    string? ExternalId,
    DateTime CreatedAt);

// ============================================================================
// REQUEST DTOs (with validation)
// ============================================================================

public record SubscriptionRequest(
    Guid WorkspaceId,
    Guid PlanId,
    Guid? UserId = null);


public record CreateSubscriptionRequest(
    [Required(ErrorMessage = WarpTalk.Shared.ApiMessageConstants.ValidationMessages.PlanIdRequired)]
    Guid PlanId);

public record TopUpCreditsRequest(
    [Required(ErrorMessage = WarpTalk.Shared.ApiMessageConstants.ValidationMessages.AmountGreaterThanZero)]
    [Range(1, int.MaxValue, ErrorMessage = WarpTalk.Shared.ApiMessageConstants.ValidationMessages.AmountGreaterThanZero)]
    int Amount);

public record CancelSubscriptionRequest(
    string? CancellationReason = null);

