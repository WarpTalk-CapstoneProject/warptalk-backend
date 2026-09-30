using System.Globalization;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services.PaymentEventHandlers;

/// <summary>
/// #466 — RENEWAL IS DRIVEN BY STRIPE.
///
/// A card customer with auto-renew on has a Stripe Subscription. Stripe charges the saved card at
/// the end of every period and tells us with a webhook; only that webhook moves the row:
///
///   * invoice.paid (subscription_cycle)      → extend the period to the invoice's, grant the cycle's
///                                              credits ONCE, write the ledger, clear any dunning
///                                              or suspension.
///   * invoice.payment_failed (subscription_cycle) → dunning: the plan stays in force for
///                                              billing.dunning.grace_days while Stripe retries,
///                                              and the owner is told to update the card.
///
/// EXACTLY ONCE. A Stripe invoice id is the idempotency key three times over: the payment row
/// (payments.provider_transaction_id is unique; PaymentAppService short-circuits a paid replay
/// before this runs), the ledger row (credit_transactions.idempotency_key is unique), and the xmin
/// token on the subscription for two deliveries racing inside the same second. The cycle-close
/// worker never selects a Stripe-owned row (SubscriptionOwnership), so it cannot grant the same
/// cycle a second time.
///
/// A Stripe subscription not yet linked to a row — every plan bought before #466 — is handled the
/// way it always was (the activation path) and linked on the way, so its NEXT renewal takes the
/// path above.
/// </summary>
public sealed partial class SubscriptionPaymentEventHandler
{
    private async Task<Result> HandleStripeRenewalAsync(PaymentEventContext context, CancellationToken ct)
    {
        var request = context.Request;
        var linked = string.IsNullOrWhiteSpace(request.StripeSubscriptionId)
            ? null
            : await _unitOfWork.SubscriptionRepository.GetByStripeSubscriptionIdAsync(request.StripeSubscriptionId, ct);

        var paid = context.ParsedPaymentStatus == PaymentConstants.PaymentStatuses.Paid;
        var failed = context.ParsedPaymentStatus == PaymentConstants.PaymentStatuses.Failed;
        if (!paid && !failed)
        {
            return Result.Success();
        }

        if (linked is null)
        {
            return await HandleUnlinkedRenewalAsync(context, paid, ct);
        }

        if (!linked.IsActive)
        {
            var live = context.Subscription;
            if (live is not null && live.Id != linked.Id)
            {
                // A plan the workspace already replaced was charged again. The money is recorded
                // against the row it paid for (never granted to the new plan), the subscription is
                // stopped, and the log line is what a refund is issued from.
                context.Subscription = linked;
                if (paid)
                {
                    _logger.LogError(
                        "stripe_invoice_paid_for_superseded_subscription WorkspaceId={WorkspaceId} StripeSubscription={StripeSubscriptionId} Invoice={InvoiceId} Amount={Amount} {Currency}. Nothing granted; refund it.",
                        linked.WorkspaceId, request.StripeSubscriptionId, request.PaymentIntentId, request.Amount, request.Currency);
                }

                ScheduleStripeCancellation(context, linked.StripeSubscriptionId, keepStripeSubscriptionId: null);
                return Result.Success();
            }

            if (failed)
            {
                // Already ended (the grace ran out); a late retry failing changes nothing.
                context.Subscription = linked;
                return Result.Success();
            }

            // Paid after the row ended — a retry that succeeded as the grace ran out. The card was
            // charged for a cycle, so the cycle is delivered: the row comes back, with whatever
            // credits were frozen when it ended.
            linked.IsActive = true;
            linked.Status = SubscriptionConstants.SubscriptionStatuses.Active;
            linked.CancelledAt = null;
            _logger.LogWarning(
                "stripe_renewal_reactivated_ended_subscription WorkspaceId={WorkspaceId} Subscription={SubscriptionId} Invoice={InvoiceId}",
                linked.WorkspaceId, linked.Id, request.PaymentIntentId);
        }

        context.Subscription = linked;
        return paid
            ? await RenewFromStripeInvoiceAsync(context, linked, ct)
            : await EnterDunningAsync(context, linked, ct);
    }

    /// <summary>
    /// The Stripe subscription is not linked to any row: a plan bought before #466 (its checkout
    /// never recorded the subscription id). Today's behaviour is kept — the activation path renews
    /// it — and the row is linked, so from now on Stripe owns it.
    /// </summary>
    private async Task<Result> HandleUnlinkedRenewalAsync(PaymentEventContext context, bool paid, CancellationToken ct)
    {
        var request = context.Request;
        var live = context.Subscription;
        if (live is not null && live.IsStripeManaged && live.StripeSubscriptionId != request.StripeSubscriptionId)
        {
            // The workspace's live plan is charged by a DIFFERENT Stripe subscription, so this one
            // is a stray (an old plan's). Record, never grant, stop it.
            if (paid)
            {
                _logger.LogError(
                    "stripe_invoice_paid_for_unlinked_subscription WorkspaceId={WorkspaceId} StripeSubscription={StripeSubscriptionId} Invoice={InvoiceId}. Nothing granted; refund it.",
                    context.WorkspaceId, request.StripeSubscriptionId, request.PaymentIntentId);
            }

            ScheduleStripeCancellation(context, request.StripeSubscriptionId, keepStripeSubscriptionId: null);
            return Result.Success();
        }

        if (!paid)
        {
            if (live is null)
            {
                // Nothing to put into dunning, and no row to hang a payment on.
                return Result.Failure(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.NotFound);
            }

            Link(live, request.StripeSubscriptionId, request.StripeCustomerId);
            return await EnterDunningAsync(context, live, ct);
        }

        var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(
            p => p.Slug.ToLower() == request.PlanSlug.ToLower() && p.DeletedAt == null,
            ct);
        if (plan is null)
        {
            _logger.LogError(BillingMessageConstants.LogMessages.PlanNotFoundForSubscription, request.PlanSlug);
            return Result.Failure(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);
        }

        var subscription = await ActivateSubscriptionAsync(context, plan, ct);
        Link(subscription, request.StripeSubscriptionId, request.StripeCustomerId);
        ClearDunning(subscription);
        subscription.TrialEndsAt = null;
        if (request.PeriodEnd is { } periodEnd && periodEnd > subscription.CurrentPeriodEnd)
        {
            subscription.CurrentPeriodEnd = periodEnd;
        }

        var topupTx = CreditMapper.CreateStripeSubscriptionTransaction(
            new StripeSubscriptionTransactionRequest(subscription, plan, request.PaymentType, context.UserId, context.PaymentId));
        topupTx.IdempotencyKey = InvoiceGrantKey(request.PaymentIntentId);
        await _unitOfWork.CreditTransactionRepository.AddAsync(topupTx, ct);

        if (_creditFreeze is not null)
        {
            await _creditFreeze.StageReleaseIntoAsync(subscription, DateTime.UtcNow, ct);
        }

        context.Subscription = subscription;
        context.SubscriptionChanged = true;
        return Result.Success();
    }

    private async Task<Result> RenewFromStripeInvoiceAsync(PaymentEventContext context, Subscription subscription, CancellationToken ct)
    {
        var request = context.Request;
        var now = DateTime.UtcNow;
        var plan = subscription.Plan ?? await _unitOfWork.Plans.GetByIdAsync(subscription.PlanId, ct);
        if (plan is null)
        {
            return Result.Failure(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);
        }

        subscription.Plan = plan;
        var creditsPerCycle = subscription.CreditsPerCycleOverride ?? plan.CreditsPerCycle;

        // The period the invoice paid for is Stripe's, and it is the truth: the row's period is
        // aligned to it rather than recomputed. It never moves BACKWARDS — a period the customer
        // already holds is not taken away by a replay or an out-of-order delivery.
        var newStart = request.PeriodStart ?? subscription.CurrentPeriodEnd;
        var newEnd = request.PeriodEnd
                     ?? Domain.Services.BillingCycleResolver.AddOneCycle(
                         newStart,
                         string.IsNullOrWhiteSpace(request.BillingCycle) ? plan.BillingCycle : request.BillingCycle);
        if (newEnd <= subscription.CurrentPeriodEnd)
        {
            _logger.LogWarning(
                "stripe_renewal_period_not_later WorkspaceId={WorkspaceId} Invoice={InvoiceId} InvoicePeriodEnd={InvoiceEnd:o} RowPeriodEnd={RowEnd:o}; credits granted, period kept.",
                subscription.WorkspaceId, request.PaymentIntentId, newEnd, subscription.CurrentPeriodEnd);
            newEnd = subscription.CurrentPeriodEnd;
        }

        // The same end-of-cycle rule the cycle close applies (rollover cap, then the new cycle's
        // credits, overage and suspension cleared), so a card customer and an invoice customer
        // renew identically.
        _domainService.RenewCycle(subscription);
        subscription.CurrentPeriodStart = newStart < newEnd ? newStart : subscription.CurrentPeriodStart;
        subscription.CurrentPeriodEnd = newEnd;
        subscription.Status = SubscriptionConstants.SubscriptionStatuses.Active;
        subscription.StripeSubscriptionStatus = SubscriptionConstants.StripeSubscriptionStatuses.Active;
        if (!string.IsNullOrWhiteSpace(request.StripeCustomerId))
        {
            subscription.StripeCustomerId = request.StripeCustomerId;
        }

        ClearDunning(subscription);
        subscription.UpdatedAt = now;

        var renewalTx = subscription.CreateRenewalTransaction(plan, newStart);
        renewalTx.Amount = creditsPerCycle;
        renewalTx.BalanceAfter = subscription.CreditsRemaining;
        renewalTx.UserId = context.UserId != Guid.Empty ? context.UserId : subscription.UserId;
        renewalTx.ReferenceId = context.PaymentId;
        renewalTx.ReferenceType = TransactionConstants.ReferenceTypes.StripePayment;
        renewalTx.IdempotencyKey = InvoiceGrantKey(request.PaymentIntentId);
        await _unitOfWork.CreditTransactionRepository.AddAsync(renewalTx, ct);

        if (_creditFreeze is not null)
        {
            await _creditFreeze.StageReleaseIntoAsync(subscription, now, ct);
        }

        context.SubscriptionChanged = true;
        _logger.LogInformation(
            "stripe_renewal_granted WorkspaceId={WorkspaceId} Subscription={SubscriptionId} Invoice={InvoiceId} Credits={Credits} PeriodEnd={PeriodEnd:o}",
            subscription.WorkspaceId, subscription.Id, request.PaymentIntentId, creditsPerCycle, newEnd);
        return Result.Success();
    }

    /// <summary>
    /// A renewal charge failed. The FIRST failure of an episode stamps the grace window (read from
    /// the platform setting then, so a later change applies to the next episode) and tells the owner;
    /// Stripe's own retries of the same invoice only refresh the reason. The row stays active and its
    /// plan in force (Subscription.GrantsPlanEntitlements) until the grace ends, when the expiry sweep
    /// — the one local worker that may touch a Stripe-owned row, and only then — ends it.
    /// </summary>
    private async Task<Result> EnterDunningAsync(PaymentEventContext context, Subscription subscription, CancellationToken ct)
    {
        var request = context.Request;
        var now = DateTime.UtcNow;
        var reason = string.IsNullOrWhiteSpace(request.FailureReason)
            ? PaymentConstants.StripePlaceholders.DefaultPaymentFailureReason
            : request.FailureReason;
        subscription.PaymentFailureReason = reason.Length > SubscriptionConstants.Dunning.FailureReasonMaxLength
            ? reason[..SubscriptionConstants.Dunning.FailureReasonMaxLength]
            : reason;
        subscription.StripeSubscriptionStatus = SubscriptionConstants.StripeSubscriptionStatuses.PastDue;
        subscription.UpdatedAt = now;

        if (subscription.PaymentFailedAt is not null)
        {
            return Result.Success();
        }

        var graceDays = _settings is null
            ? SubscriptionConstants.Dunning.DefaultGraceDays
            : await _settings.GetInt32Async(SubscriptionConstants.Dunning.GraceDaysKey, SubscriptionConstants.Dunning.DefaultGraceDays, ct: ct);
        graceDays = Math.Max(0, graceDays);

        subscription.PaymentFailedAt = now;
        subscription.PaymentGraceEndsAt = now.AddDays(graceDays);
        _logger.LogWarning(
            "stripe_renewal_payment_failed WorkspaceId={WorkspaceId} Subscription={SubscriptionId} Invoice={InvoiceId} GraceEndsAt={GraceEndsAt:o}",
            subscription.WorkspaceId, subscription.Id, request.PaymentIntentId, subscription.PaymentGraceEndsAt);

        if (_notifications is not null)
        {
            var owner = subscription.UserId;
            var graceEnds = subscription.PaymentGraceEndsAt.Value;
            var workspaceId = subscription.WorkspaceId;
            context.AfterCommit.Add(async token =>
            {
                await _notifications.SendNotificationsAsync(
                    new SendBillingNotificationsRequest(
                        new[] { owner },
                        BillingMessageConstants.Notifications.Types.SubscriptionChanged,
                        BillingMessageConstants.NotificationTitles.RenewalPaymentFailed,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            BillingMessageConstants.NotificationTitles.RenewalPaymentFailedBody,
                            graceEnds.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)),
                        BillingMessageConstants.Notifications.ActionUrls.Billing,
                        new Dictionary<string, string>
                        {
                            [BillingMessageConstants.NotificationMetadataKeys.WorkspaceId] = workspaceId.ToString(),
                            [BillingMessageConstants.NotificationMetadataKeys.GraceEndsAt] = graceEnds.ToString("o", CultureInfo.InvariantCulture),
                        }),
                    token);
            });
        }

        return Result.Success();
    }

    /// <summary>
    /// A checkout just activated <paramref name="subscription"/>: record who renews it. A
    /// subscription-mode checkout carries its Stripe subscription id — Stripe owns the row from now
    /// on. A one-off payment carries none — nothing renews it. Either way a paid plan is no longer a
    /// trial, and any dunning state belonged to what came before.
    /// </summary>
    private void LinkCheckoutToStripe(PaymentEventContext context, Subscription subscription)
    {
        var request = context.Request;
        ScheduleStripeCancellation(context, subscription.StripeSubscriptionId, request.StripeSubscriptionId);

        if (!string.IsNullOrWhiteSpace(request.StripeSubscriptionId))
        {
            Link(subscription, request.StripeSubscriptionId, request.StripeCustomerId);
            subscription.AutoRenew = true;
        }
        else
        {
            subscription.RenewalMode = SubscriptionConstants.RenewalModes.None;
            subscription.StripeSubscriptionId = null;
            subscription.StripeSubscriptionStatus = null;
            subscription.AutoRenew = false;
            if (!string.IsNullOrWhiteSpace(request.StripeCustomerId))
            {
                subscription.StripeCustomerId = request.StripeCustomerId;
            }
        }

        subscription.TrialEndsAt = null;
        ClearDunning(subscription);
    }

    private static void Link(Subscription subscription, string stripeSubscriptionId, string? stripeCustomerId)
    {
        subscription.RenewalMode = SubscriptionConstants.RenewalModes.Stripe;
        subscription.StripeSubscriptionId = stripeSubscriptionId;
        subscription.StripeSubscriptionStatus ??= SubscriptionConstants.StripeSubscriptionStatuses.Active;
        if (!string.IsNullOrWhiteSpace(stripeCustomerId))
        {
            subscription.StripeCustomerId = stripeCustomerId;
        }
    }

    private static void ClearDunning(Subscription subscription)
    {
        subscription.PaymentFailedAt = null;
        subscription.PaymentGraceEndsAt = null;
        subscription.PaymentFailureReason = null;
    }

    /// <summary>
    /// Queues the cancellation of a Stripe subscription that must stop charging, unless it is the
    /// one being kept. After the commit, best-effort: Stripe answering late is logged, and the
    /// row no longer points at it either way.
    /// </summary>
    private void ScheduleStripeCancellation(PaymentEventContext context, string? stripeSubscriptionId, string? keepStripeSubscriptionId)
    {
        if (_recurring is null
            || string.IsNullOrWhiteSpace(stripeSubscriptionId)
            || string.Equals(stripeSubscriptionId, keepStripeSubscriptionId, StringComparison.Ordinal))
        {
            return;
        }

        var recurring = _recurring;
        context.AfterCommit.Add(async token =>
        {
            var cancelled = await recurring.CancelNowAsync(stripeSubscriptionId, token);
            if (!cancelled.IsSuccess)
            {
                _logger.LogError(
                    "stripe_replaced_subscription_cancel_failed StripeSubscription={StripeSubscriptionId} Error={Error}. It may charge again; cancel it in Stripe.",
                    stripeSubscriptionId, cancelled.Error);
            }
        });
    }

    /// <summary>The ledger idempotency key of the credits a Stripe invoice granted.</summary>
    public static string InvoiceGrantKey(string stripeInvoiceId) => $"stripe_invoice:{stripeInvoiceId}:cycle_grant";
}
