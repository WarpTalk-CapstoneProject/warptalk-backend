using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(IUnitOfWork unitOfWork, ILogger<SubscriptionService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<SubscriptionDto>> GetActiveSubscriptionAsync(
        Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<SubscriptionDto>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            return Result.Success(sub.ToDto(plan?.Name ?? string.Empty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active subscription for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<SubscriptionDto>> CreateSubscriptionAsync(
        CreateSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Id == request.PlanId && p.IsActive && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<SubscriptionDto>(
                    $"Plan '{request.PlanId}' not found or inactive.",
                    ErrorCodes.BillingPlanNotFound);

            var existing = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.WorkspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (existing is not null)
                return Result.Failure<SubscriptionDto>(
                    "This workspace already has an active subscription.",
                    ErrorCodes.BillingSubscriptionAlreadyActive);

            var subscription = request.ToEntity(plan);

            await _unitOfWork.SubscriptionRepository.AddAsync(subscription, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(subscription.ToDto(plan.Name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating subscription for WorkspaceId {WorkspaceId} and PlanId {PlanId}", request.WorkspaceId, request.PlanId);
            return Result.Failure<SubscriptionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<SubscriptionDto>> CancelSubscriptionAsync(
        Guid workspaceId, string? reason, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<SubscriptionDto>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            var now = DateTime.UtcNow;
            sub.Status = "cancelled";
            sub.CancellationReason = reason;
            sub.CancelledAt = now;
            sub.IsActive = false;
            sub.UpdatedAt = now;

            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            return Result.Success(sub.ToDto(plan?.Name ?? string.Empty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling subscription for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}
