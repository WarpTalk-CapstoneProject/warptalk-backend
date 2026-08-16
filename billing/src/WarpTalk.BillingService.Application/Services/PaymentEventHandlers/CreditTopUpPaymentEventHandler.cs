using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services.PaymentEventHandlers;

/// <summary>
/// WT-429 — grants the credits a completed top-up paid for.
///
/// This handler is the piece that never existed. "CreditTopUp" has been the payment type the web
/// posts since the top-up UI was built, no registered handler claimed it, and
/// PaymentAppService's `if (handler is not null)` had no else — so the request wrote a payment
/// row, issued an invoice, returned success, and granted nothing. Money in, credits out of
/// nowhere. That is why the button was switched off (#190) rather than repaired at the time; this
/// is the repair.
///
/// HOW MANY CREDITS
///   From the Stripe session metadata, written at checkout creation from a SERVER-side price
///   (PaymentAppService reads credit_value_vnd out of billing_pricing_config). Deriving it here
///   from Amount ÷ rate would make the count depend on a rate that may have changed between
///   checkout and completion, and would silently re-price a payment the customer already
///   authorised at a quoted number.
///
/// IDEMPOTENCE
///   Both completion paths run for a given session — the webhook and the return-page read,
///   whichever arrives first. PaymentAppService's already-processed guard (keyed on the provider
///   transaction id) short-circuits the second one before any handler runs, and the credit
///   transaction row this writes carries the same reference so a double grant is visible in the
///   ledger rather than merely absent.
/// </summary>
public sealed class CreditTopUpPaymentEventHandler : IPaymentEventHandler
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditTopUpPaymentEventHandler> _logger;

    public CreditTopUpPaymentEventHandler(
        IUnitOfWork unitOfWork,
        ILogger<CreditTopUpPaymentEventHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public bool CanHandle(PaymentEventContext context)
        => string.Equals(
            context.Request.PaymentType,
            PaymentConstants.PaymentTypes.CreditTopUp,
            StringComparison.OrdinalIgnoreCase);

    public async Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default)
    {
        // Unpaid states reach here for failed/disputed webhooks too. Nothing to grant, and
        // nothing to complain about.
        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid)
        {
            return Result.Success();
        }

        var credits = context.Request.Credits;
        if (credits <= 0)
        {
            // A paid top-up that carries no credit count is the old bug in a new place: the money
            // is taken and there is nothing to grant. Fail loudly rather than complete quietly —
            // PaymentAppService surfaces this, and the payment row is not written, so the
            // discrepancy is investigable instead of invisible.
            _logger.LogError(
                "credit_topup_missing_credit_count: StripeSessionId={SessionId} WorkspaceId={WorkspaceId} "
                + "Amount={Amount} {Currency}. The session carried no Credits metadata.",
                context.Request.StripeSessionId,
                context.WorkspaceId,
                context.Request.Amount,
                context.Request.Currency);

            return Result.Failure(
                BillingMessageConstants.ErrorMessages.CreditTopUpMissingCreditCount,
                ErrorCodes.ValidationError);
        }

        // Credits live on the subscription — it is what CreditsRemaining hangs off and what every
        // consumption path decrements. A workspace with no subscription has nowhere to put them.
        var subscription = context.Subscription
            ?? await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == context.WorkspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

        if (subscription is null)
        {
            _logger.LogError(
                "credit_topup_no_subscription: StripeSessionId={SessionId} WorkspaceId={WorkspaceId}. "
                + "The payment succeeded but there is no active subscription to credit.",
                context.Request.StripeSessionId,
                context.WorkspaceId);

            return Result.Failure(
                BillingMessageConstants.ErrorMessages.CreditTopUpNoSubscription,
                ErrorCodes.InvalidState);
        }

        subscription.CreditsRemaining += credits;
        subscription.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.SubscriptionRepository.Update(subscription);

        await _unitOfWork.CreditTransactionRepository.AddAsync(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            WorkspaceId = context.WorkspaceId,
            UserId = context.UserId,
            Amount = credits,
            Type = TransactionConstants.TransactionTypes.TopUp,
            Description = string.Format(
                BillingMessageConstants.SuccessMessages.CreditTopUpGrantedTemplate,
                credits),
            // The Stripe payment, so the ledger row and the money that caused it are joinable.
            ReferenceId = context.PaymentId,
            ReferenceType = TransactionConstants.ReferenceTypes.StripePayment,
            BalanceAfter = subscription.CreditsRemaining,
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken);

        // The caller commits; SubscriptionChanged makes it publish the entitlement refresh, so
        // consumers see the new balance without waiting for the hourly reconcile.
        context.Subscription = subscription;
        context.SubscriptionChanged = true;

        _logger.LogInformation(
            "credit_topup_granted: Credits={Credits} WorkspaceId={WorkspaceId} SubscriptionId={SubscriptionId} BalanceAfter={BalanceAfter}",
            credits,
            context.WorkspaceId,
            subscription.Id,
            subscription.CreditsRemaining);

        return Result.Success();
    }
}
