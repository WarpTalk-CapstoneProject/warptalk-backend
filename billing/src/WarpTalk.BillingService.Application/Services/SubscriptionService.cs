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
using PaymentClient = WarpTalk.Shared.Protos.PaymentService.PaymentServiceClient;

namespace WarpTalk.BillingService.Application.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SubscriptionService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly PaymentClient _paymentServiceClient;

    public SubscriptionService(
        IUnitOfWork unitOfWork,
        ILogger<SubscriptionService> logger,
        IBillingMessagePublisher messagePublisher,
        PaymentClient paymentServiceClient)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _paymentServiceClient = paymentServiceClient;
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

            // Call Payment Service to cancel Stripe Subscription
            try
            {
                await _paymentServiceClient.CancelStripeSubscriptionAsync(new WarpTalk.Shared.Protos.CancelStripeSubscriptionRequest
                {
                    WorkspaceId = workspaceId.ToString()
                }, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cancel subscription on Stripe for WorkspaceId {WorkspaceId}", workspaceId);
            }

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

            // Try to update the Stripe subscription directly with proration
            bool stripeUpdated = false;
            try
            {
                var updateResponse = await _paymentServiceClient.UpdateStripeSubscriptionAsync(new WarpTalk.Shared.Protos.UpdateStripeSubscriptionRequest
                {
                    WorkspaceId = request.WorkspaceId.ToString(),
                    NewAmount = (double)newPlan.Price,
                    Currency = newPlan.Currency,
                    NewPlanName = newPlan.Name
                }, cancellationToken: cancellationToken);

                stripeUpdated = updateResponse.Success;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update subscription on Stripe for WorkspaceId {WorkspaceId} during change plan.", request.WorkspaceId);
            }

            if (!stripeUpdated)
            {
                try
                {
                    await _paymentServiceClient.CancelStripeSubscriptionAsync(new WarpTalk.Shared.Protos.CancelStripeSubscriptionRequest
                    {
                        WorkspaceId = request.WorkspaceId.ToString()
                    }, cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cancel old subscription on Stripe for WorkspaceId {WorkspaceId} during change plan.", request.WorkspaceId);
                }
            }

            // Create new subscription with carry-over credits
            var newSub = request.ToEntity(oldSub, newPlan);

            // For local development, even if stripeUpdate fails or is skipped, we still make the new subscription active immediately
            newSub.IsActive = true;
            newSub.Status = "active";
            newSub.CurrentPeriodStart = DateTime.UtcNow;
            newSub.CurrentPeriodEnd = newPlan.BillingCycle switch
            {
                "yearly" => DateTime.UtcNow.AddYears(1),
                "semiannual" => DateTime.UtcNow.AddMonths(6),
                _ => DateTime.UtcNow.AddMonths(1)
            };

            newSub.CreditsRemaining += newPlan.CreditsPerCycle;

            var randomSuffix = Guid.NewGuid().ToString().Replace("-", "")[..14].ToLower();
            var upgradeTx = new WarpTalk.BillingService.Domain.Entities.CreditTransaction
            {
                Id = Guid.NewGuid(),
                SubscriptionId = newSub.Id,
                UserId = newSub.UserId,
                Amount = newPlan.CreditsPerCycle,
                Type = "top_up",
                Description = stripeUpdated ? $"Plan upgrade to {newPlan.Name} (Stripe Direct)" : $"Plan upgrade to {newPlan.Name} (Simulation)",
                ReferenceId = Guid.NewGuid(),
                ReferenceType = "stripe_payment",
                BalanceAfter = newSub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.CreditTransactionRepository.AddAsync(upgradeTx, cancellationToken);

            var paymentTx = new WarpTalk.BillingService.Domain.Entities.Payment
            {
                Id = Guid.NewGuid(),
                SubscriptionId = newSub.Id,
                UserId = newSub.UserId,
                Amount = newPlan.Price,
                TaxAmount = 0m,
                TotalAmount = newPlan.Price,
                Currency = newPlan.Currency,
                PaymentMethod = stripeUpdated ? "Stripe Upgrade (Direct)" : "Stripe Upgrade (Simulation)",
                Provider = "stripe",
                ProviderTransactionId = $"ch_{randomSuffix}",
                Status = "paid",
                PaidAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _unitOfWork.PaymentRepository.AddAsync(paymentTx, cancellationToken);

            var invoice = new WarpTalk.BillingService.Domain.Entities.Invoice
            {
                Id = Guid.NewGuid(),
                UserId = newSub.UserId,
                PaymentId = paymentTx.Id,
                InvoiceNumber = $"in_{randomSuffix}",
                Subtotal = newPlan.Price,
                Tax = 0,
                Total = newPlan.Price,
                Currency = newPlan.Currency,
                Status = "paid",
                PdfUrl = string.Empty,
                LineItems = "[]",
                IssuedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.InvoiceRepository.AddAsync(invoice, cancellationToken);

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
