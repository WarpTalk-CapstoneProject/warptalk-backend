using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ISubscriptionManagementService
{
    // --- Plan Methods ---
    Task<Result<IEnumerable<PlanDto>>> GetActivePlansAsync(
        CancellationToken cancellationToken = default);

    Task<Result<PlanDto>> GetPlanByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<PlanDto>> GetPlanBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default);

    Task<Result<PlanDto>> CreatePlanAsync(
        CreatePlanRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<PlanDto>> UpdatePlanAsync(
        Guid id,
        UpdatePlanRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<bool>> DeactivatePlanAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // --- Subscription Methods ---
    Task<Result<SubscriptionDto>> GetActiveSubscriptionAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<Result<SubscriptionDto>> CreateSubscriptionAsync(
        CreateSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<bool>> CancelSubscriptionAsync(
        Guid workspaceId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<Result<SubscriptionDto>> ChangeSubscriptionAsync(
        ChangeSubscriptionRequest request,
        CancellationToken cancellationToken = default);
}
