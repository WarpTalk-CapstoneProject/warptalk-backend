using System.ComponentModel.DataAnnotations;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

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
    string? WorkspaceName = null,
    int? CreditsPerCycleOverride = null,
    decimal? ContractPriceVnd = null,
    int? OverageCapCreditsOverride = null,
    decimal? OveragePricePerCreditOverride = null,
    int? InvoiceTermsDaysOverride = null,
    string? BillingContactEmail = null,
    int EffectiveCreditsPerCycle = 0,
    decimal EffectiveContractPriceVnd = 0,
    int EffectiveOverageCapCredits = 0,
    decimal EffectiveOveragePricePerCredit = 0,
    int EffectiveInvoiceTermsDays = 0,
    int OverageCreditsThisCycle = 0,
    DateTime? OverageStartedAt = null,
    string ServiceState = SubscriptionConstants.ServiceStates.Healthy,
    string? SuspendedReason = null,
    DateTime? TrialEndsAt = null
)
{
    public int CurrentCredits => CreditsRemaining;
    public DateTime StartDate => CurrentPeriodStart;
    public DateTime? EndDate => CurrentPeriodEnd;
}



// ============================================================================
// REQUEST DTOs (with validation)
// ============================================================================

public record CreateWorkspaceContractSubscriptionRequest(
    Guid WorkspaceId,
    Guid PlanId,
    UpdateSubscriptionContractTermsRequest ContractTerms,
    Guid? UserId = null) : IWorkspaceScopedRequest;

public record TrialSubscriptionRequest(
    Guid WorkspaceId,
    Guid UserId,
    string OwnerEmail) : IWorkspaceScopedRequest;

public record ResumeSubscriptionRequest(
    string? Reason = null);

public record UpdateSubscriptionContractTermsRequest(
    int? CreditsPerCycleOverride = null,
    decimal? ContractPriceVnd = null,
    int? OverageCapCreditsOverride = null,
    decimal? OveragePricePerCreditOverride = null,
    int? InvoiceTermsDaysOverride = null,
    string? BillingContactEmail = null);
