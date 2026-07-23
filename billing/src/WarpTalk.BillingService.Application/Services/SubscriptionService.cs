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
using WarpTalk.Shared.Protos;

using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SubscriptionService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IStripePaymentService _stripePaymentService;

    public SubscriptionService(
        IUnitOfWork unitOfWork,
        ILogger<SubscriptionService> logger,
        IBillingMessagePublisher messagePublisher,
        IStripePaymentService stripePaymentService)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _stripePaymentService = stripePaymentService;
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
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            return Result.Success(sub.ToDto(plan?.Name ?? "Unknown Plan", plan?.Price ?? 0m));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching active subscription for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PaginatedResponse<SubscriptionDto>>> GetGlobalSubscriptionsAsync(
        PaginationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var size = Math.Clamp(query.PageSize, 1, 200);
            var skip = (Math.Max(1, query.PageNumber) - 1) * size;

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
                items.Add(sub.ToDto(plan?.Name ?? BillingConstants.PlanAuditMessages.UnknownPlan, plan?.Price ?? 0m));
            }

            // Resolve workspace names cross-schema
            try
            {
                var workspaceIds = items
                    .Where(i => i.WorkspaceId.HasValue && i.WorkspaceId != Guid.Empty)
                    .Select(i => i.WorkspaceId!.Value)
                    .Distinct()
                    .ToArray();

                if (workspaceIds.Length > 0)
                {
                    var workspaceNames = await _unitOfWork.CreditTransactionRepository.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);

                    items = items.Select(i =>
                        i.WorkspaceId.HasValue && workspaceNames.TryGetValue(i.WorkspaceId.Value, out var wName)
                            ? i with { WorkspaceName = wName }
                            : i
                    ).ToList();
                }
            }
            catch (Exception wsEx)
            {
                _logger.LogWarning(wsEx, BillingConstants.LogMessages.FailedToResolveWorkspaceNamesGlobalSub);
            }

            return Result.Success(PaginatedResponse<SubscriptionDto>.Create(items, total, Math.Max(1, query.PageNumber), size));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingConstants.LogMessages.ErrorFetchingGlobalSubscriptions);
            return Result.Failure<PaginatedResponse<SubscriptionDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
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
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            var existing = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.WorkspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (existing is not null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionAlreadyActive,
                    ErrorCodes.BillingSubscriptionAlreadyActive);

            var subscription = request.ToEntity(plan);

            await _unitOfWork.SubscriptionRepository.AddAsync(subscription, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishRealtimeUpdateAsync(subscription.UserId, BillingConstants.Notifications.ActionCreated, plan.Name, cancellationToken);

            return Result.Success(subscription.ToDto(plan.Name, plan.Price));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingConstants.LogMessages.ErrorCreatingSubscription, request.WorkspaceId, request.PlanId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
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
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            sub.Cancel(reason);

            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);

            // Call Stripe service to cancel Stripe Subscription
            try
            {
                await _stripePaymentService.CancelSubscriptionAsync(workspaceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, BillingConstants.LogMessages.ErrorCancellingStripeSubscription, workspaceId);
            }

            await PublishRealtimeUpdateAsync(sub.UserId, BillingConstants.Notifications.ActionCancelled, plan?.Name ?? BillingConstants.PlanAuditMessages.UnknownPlan, cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling subscription for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
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
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            if (oldSub.PlanId == request.PlanId)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionAlreadyActive,
                    ErrorCodes.BillingSubscriptionAlreadyActive);

            var newPlan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Id == request.PlanId && p.IsActive && p.DeletedAt == null,
                cancellationToken);

            if (newPlan is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);
                    
            // Try to update the Stripe subscription directly with proration
            bool stripeUpdated = false;
            try
            {
                stripeUpdated = await _stripePaymentService.UpdateSubscriptionAsync(request.WorkspaceId, newPlan.Price, newPlan.Currency, newPlan.Slug);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, BillingConstants.LogMessages.FailedToUpdateStripeSubChangePlan, request.WorkspaceId);
            }

            if (!stripeUpdated)
            {
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingStripeUpdateFailed,
                    ErrorCodes.InternalServerError);
            }

            // With Webhook architecture, we do not update local DB synchronously.
            // The local DB will be updated when Stripe sends the customer.subscription.updated webhook.
            // We return a "Pending" DTO to the client so the UI can show a loading state.
            var pendingSub = request.ToEntity(oldSub, newPlan);
            pendingSub.Status = BillingConstants.SubscriptionStatuses.Pending;
            
            return Result.Success(pendingSub.ToDto(newPlan.Name, newPlan.Price));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing subscription for WorkspaceId {WorkspaceId} to PlanId {PlanId}", request.WorkspaceId, request.PlanId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    private async Task PublishRealtimeUpdateAsync(Guid userId, string action, string planName, CancellationToken cancellationToken)
    {
        try
        {
            var msg = NotificationMapper.ToSubscriptionChangedMessage(userId, action, planName);
            await _messagePublisher.PublishAsync(BillingConstants.Notifications.Channel, msg, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, BillingConstants.LogMessages.FailedToPublishRealtimeSubscriptionUpdate, userId);
        }
    }
}
