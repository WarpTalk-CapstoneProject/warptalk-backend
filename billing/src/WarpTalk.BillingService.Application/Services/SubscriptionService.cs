using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IWorkspaceDirectory? _workspaceDirectory;

    public SubscriptionService(
        IUnitOfWork unitOfWork,
        ILogger<SubscriptionService> logger,
        IBillingMessagePublisher messagePublisher,
        IWorkspaceDirectory? workspaceDirectory = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _workspaceDirectory = workspaceDirectory;
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
            return Result.Success(sub.ToDto(plan?.Name ?? "Unknown Plan", plan?.Price ?? 0m));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching active subscription for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PagedResult<SubscriptionDto>>> GetGlobalSubscriptionsAsync(
        int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            var size = pageSize > 0 ? pageSize : 20;
            var skip = ((pageNumber > 0 ? pageNumber : 1) - 1) * size;

            var subs = await _unitOfWork.SubscriptionRepository.GetPagedAsync(
                s => s.DeletedAt == null,
                skip, size,
                q => q.OrderByDescending(sub => sub.CreatedAt),
                cancellationToken: cancellationToken);

            var total = await _unitOfWork.SubscriptionRepository.CountAsync(
                s => s.DeletedAt == null,
                cancellationToken);

            var items = new List<SubscriptionDto>();
            foreach (var sub in subs)
            {
                var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
                items.Add(sub.ToDto(plan?.Name ?? "Unknown Plan", plan?.Price ?? 0m));
            }

            // Resolve display names through the Workspace service boundary.
            try
            {
                var workspaceIds = items
                    .Where(i => i.WorkspaceId.HasValue && i.WorkspaceId != Guid.Empty)
                    .Select(i => i.WorkspaceId!.Value)
                    .Distinct()
                    .ToArray();

                if (workspaceIds.Length > 0 && _workspaceDirectory is not null)
                {
                    var workspaceNames = await _workspaceDirectory.GetNamesAsync(
                        workspaceIds,
                        cancellationToken);

                    items = items.Select(i =>
                        i.WorkspaceId.HasValue && workspaceNames.TryGetValue(i.WorkspaceId.Value, out var wName)
                            ? i with { WorkspaceName = wName }
                            : i
                    ).ToList();
                }
            }
            catch (Exception wsEx)
            {
                _logger.LogWarning(wsEx, "Failed to resolve workspace names for global subscriptions history");
            }

            return Result.Success(new PagedResult<SubscriptionDto>(total, items));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching global subscriptions");
            return Result.Failure<PagedResult<SubscriptionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<SubscriptionDto>> CreateSubscriptionAsync(
        SubscriptionRequest request, CancellationToken cancellationToken = default)
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

            await PublishRealtimeUpdateAsync(subscription.UserId, "created", plan.Name, cancellationToken);

            return Result.Success(subscription.ToDto(plan.Name, plan.Price));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating subscription for WorkspaceId {WorkspaceId} and PlanId {PlanId}", request.WorkspaceId, request.PlanId);
            return Result.Failure<SubscriptionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<bool>> CancelSubscriptionAsync(
        Guid workspaceId, string? reason, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<bool>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            sub.Cancel(reason);

            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);

            await PublishRealtimeUpdateAsync(sub.UserId, "cancelled", plan?.Name ?? "Unknown Plan", cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling subscription for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<bool>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<SubscriptionDto>> ChangeSubscriptionAsync(
        SubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var oldSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.WorkspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (oldSub is null)
                return Result.Failure<SubscriptionDto>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            if (oldSub.PlanId == request.PlanId)
                return Result.Failure<SubscriptionDto>(
                    "The workspace is already subscribed to this plan.",
                    ErrorCodes.BillingSubscriptionAlreadyActive);

            var newPlan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Id == request.PlanId && p.IsActive && p.DeletedAt == null,
                cancellationToken);

            if (newPlan is null)
                return Result.Failure<SubscriptionDto>(
                    $"New Plan '{request.PlanId}' not found or inactive.",
                    ErrorCodes.BillingPlanNotFound);

            oldSub.CancelImmediately("upgraded/downgraded");
            _unitOfWork.SubscriptionRepository.Update(oldSub);

            // Create new subscription with carry-over credits
            var newSub = request.ToEntity(oldSub, newPlan);

            // The replacement remains pending until the provider confirms the
            // plan-change payment. It must not grant credits or access before
            // the payment/webhook path completes.

            await _unitOfWork.SubscriptionRepository.AddAsync(newSub, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishRealtimeUpdateAsync(newSub.UserId, "changed", newPlan.Name, cancellationToken);

            return Result.Success(newSub.ToDto(newPlan.Name, newPlan.Price));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing subscription for WorkspaceId {WorkspaceId} to PlanId {PlanId}", request.WorkspaceId, request.PlanId);
            return Result.Failure<SubscriptionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    private async Task PublishRealtimeUpdateAsync(Guid userId, string action, string planName, CancellationToken cancellationToken)
    {
        try
        {
            var msg = new WarpTalk.Shared.Models.RealtimeNotificationMessage
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId.ToString(),
                Type = "billing.subscription_changed",
                Title = "Subscription Updated",
                Content = $"Your subscription has been {action} to {planName}.",
                PayloadJson = "{}"
            };
            await _messagePublisher.PublishAsync("warptalk:notifications:new", msg, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish realtime update for user {UserId}", userId);
        }
    }
}
