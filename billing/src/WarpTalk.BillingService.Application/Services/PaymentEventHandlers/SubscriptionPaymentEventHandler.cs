using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
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
        var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(
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
        // `DeletedAt == null` mirrors the lookup in PaymentAppService.CreatePaymentEventContextAsync
        // exactly. It did not, and the difference decided which branch below runs: a soft-deleted
        // row that still carried is_active = true was INVISIBLE to that lookup — so
        // context.Subscription came back null and this method took the "create a new one" path —
        // while being visible here, where it was cancelled but left is_active = true. Migration
        // 016 puts a unique index on (workspace_id) WHERE is_active = true, so inserting the new
        // subscription then violated it, SaveChangesAsync threw, and the whole payment event
        // rolled back: money taken, nothing activated. Two queries over one table have to agree
        // on what "the workspace's subscription" means.
        var oldSubs = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.WorkspaceId == context.WorkspaceId && s.IsActive && s.DeletedAt == null && s.Id != (context.Subscription != null ? context.Subscription.Id : Guid.Empty),
            cancellationToken);

        foreach (var oldSub in oldSubs)
        {
            oldSub.AutoRenew = false;
            oldSub.Status = SubscriptionConstants.SubscriptionStatuses.Cancelled;
            // IsActive is the flag every "does this workspace have a plan" query filters on, and
            // the one migration 016's unique index is built over — Status is a label beside it.
            // Cancelling without clearing this left the superseded row still answering as the
            // workspace's active subscription and still occupying the one slot the index allows.
            oldSub.IsActive = false;
            oldSub.CancelledAt ??= DateTime.UtcNow;
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
        context.Subscription.CreditsRemaining += plan.CreditsPerCycle;
        if (context.Subscription.CreditsRemaining >= 0)
        {
            context.Subscription.OverageStartedAt = null;
            context.Subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;
            context.Subscription.SuspendedReason = null;
        }
        context.Subscription.CreditsUsedThisCycle = 0;
        context.Subscription.CurrentPeriodStart = DateTime.UtcNow;
        context.Subscription.CurrentPeriodEnd = periodEnd;
        context.Subscription.UpdatedAt = DateTime.UtcNow;
        return context.Subscription;
    }

    /// <summary>
    /// WT-370: this took <paramref name="billingCycle"/> and threw it away, returning one month
    /// for everything. Stripe was already charging the annual price on an annual interval, so a
    /// ₫1,900,000/year purchase bought twelve months of billing and thirty days of credits — the
    /// workspace stops translating in month two while the card keeps being charged once a year.
    /// </summary>
    private static DateTime CalculatePeriodEnd(string billingCycle)
        => BillingCycleResolver.AddOneCycle(DateTime.UtcNow, billingCycle);
}
