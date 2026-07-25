using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services.PaymentEventHandlers;

public sealed class SubscriptionPaymentEventHandler : IPaymentEventHandler
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SubscriptionPaymentEventHandler> _logger;

    public SubscriptionPaymentEventHandler(
        IUnitOfWork unitOfWork,
        ILogger<SubscriptionPaymentEventHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public bool CanHandle(PaymentEventContext context)
        => PaymentConstants.PaymentTypes.SubscriptionLifecycleTypes.Contains(context.Request.PaymentType);

    public async Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default)
    {
        var request = context.Request;
        var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
            p => p.Slug.ToLower() == request.PlanSlug.ToLower() && p.DeletedAt == null,
            cancellationToken);

        if (plan is null)
        {
            _logger.LogError(BillingMessageConstants.LogMessages.PlanNotFoundForSubscription, request.PlanSlug);
            return Result.Failure(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);
        }

        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid)
        {
            return Result.Success();
        }

        var subscription = await ActivateSubscriptionAsync(context, plan, cancellationToken);
        var topupTx = CreditMapper.CreateStripeSubscriptionTransaction(
            new StripeSubscriptionTransactionRequest(
                subscription,
                plan,
                request.PaymentType,
                context.UserId,
                context.PaymentId));

        await _unitOfWork.CreditTransactionRepository.AddAsync(topupTx, cancellationToken);
        context.Subscription = subscription;
        context.SubscriptionChanged = true;

        return Result.Success();
    }

    private async Task<Subscription> ActivateSubscriptionAsync(
        PaymentEventContext context,
        Plan plan,
        CancellationToken cancellationToken)
    {
        var oldSubs = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.WorkspaceId == context.WorkspaceId && s.IsActive && s.Id != (context.Subscription != null ? context.Subscription.Id : Guid.Empty),
            cancellationToken);

        foreach (var oldSub in oldSubs)
        {
            oldSub.AutoRenew = false;
            oldSub.Status = SubscriptionConstants.SubscriptionStatuses.Cancelled;
            oldSub.UpdatedAt = DateTime.UtcNow;
        }

        var periodEnd = CalculatePeriodEnd(context.Request.BillingCycle);
        if (context.Subscription is null)
        {
            var newSubscription = SubscriptionMapper.CreateNewStripeSubscription(context.WorkspaceId, context.UserId, plan, periodEnd);
            await _unitOfWork.SubscriptionRepository.AddAsync(newSubscription, cancellationToken);
            return newSubscription;
        }

        context.Subscription.PlanId = plan.Id;
        context.Subscription.Status = SubscriptionConstants.SubscriptionStatuses.Active;
        context.Subscription.IsActive = true;
        context.Subscription.ApplyCycleAllocation(plan.CreditsPerCycle);
        context.Subscription.CreditsUsedThisCycle = 0;
        context.Subscription.CurrentPeriodStart = DateTime.UtcNow;
        context.Subscription.CurrentPeriodEnd = periodEnd;
        context.Subscription.UpdatedAt = DateTime.UtcNow;
        return context.Subscription;
    }

    private static DateTime CalculatePeriodEnd(string billingCycle)
        => billingCycle.ToLowerInvariant() == SubscriptionConstants.BillingCycles.Yearly
            ? DateTime.UtcNow.AddYears(1)
            : DateTime.UtcNow.AddMonths(1);
}
