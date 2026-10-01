using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public class StripeWebhookService : IStripeWebhookService
{
    private readonly IPaymentAppService _paymentAppService;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<StripeWebhookService> _logger;
    private readonly Stripe.SubscriptionService _stripeSubscriptionService;
    private readonly IStripeSubscriptionLifecycleService? _lifecycle;

    public StripeWebhookService(
        IPaymentAppService paymentAppService,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<StripeWebhookService> logger,
        Stripe.SubscriptionService stripeSubscriptionService,
        IStripeSubscriptionLifecycleService? lifecycle = null)
    {
        _paymentAppService = paymentAppService;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
        _stripeSubscriptionService = stripeSubscriptionService;
        _lifecycle = lifecycle;
    }

    public async Task<Result<bool>> HandleWebhookAsync(string jsonPayload, string signatureHeader, CancellationToken cancellationToken = default)
    {
        // WT-370 — THE LINE THAT MADE A TAKEN PAYMENT DISAPPEAR QUIETLY.
        //
        // Every branch below used to do `if (!result.IsSuccess) _logger.LogWarning(...)` and then
        // fall through to `return Result.Success(true)`. So when processing failed, this endpoint
        // answered Stripe 200 OK.
        //
        // Two consequences, and the incident is both of them:
        //
        //   1. STRIPE STOPPED TRYING. A non-2xx makes Stripe redeliver with backoff for ~3 days —
        //      free, built-in recovery that heals any transient database or infrastructure fault
        //      by itself. Answering 200 threw every one of those retries away on the first
        //      attempt. A failure that would have fixed itself became permanent.
        //
        //   2. THE DASHBOARD LIED TO EVERY LATER INVESTIGATION. Stripe showed
        //      "checkout.session.completed — 200 OK — Delivered" four times over while the
        //      workspace had no plan, so the one place anybody looks first said the webhook was
        //      fine. Triage went looking for a missing endpoint, a mode mismatch and a signature
        //      problem, and none of those were ever wrong.
        //
        // Now: a failure that a redelivery could fix is reported as a failure. A failure that a
        // redelivery cannot fix — the payload itself is unusable — is still acknowledged, because
        // asking Stripe to resend the same broken payload for three days buys nothing; it is
        // logged at Error instead so it surfaces rather than being retried into silence.
        Result? processingFailure = null;

        try
        {
            var webhookSecret = _configuration[PaymentConstants.StripeConfigKeys.WebhookSecret];
            Event stripeEvent;

            if (string.IsNullOrEmpty(webhookSecret) || webhookSecret == PaymentConstants.StripePlaceholders.WebhookSecretPlaceholder)
            {
                if (!_environment.IsDevelopment())
                {
                    _logger.LogError(PaymentConstants.StripePlaceholders.DefaultStripeWebhookProductionSecretError);
                    return Result.Failure<bool>(PaymentConstants.StripePlaceholders.WebhookSecretNotConfigured, ErrorCodes.InternalServerError);
                }

                stripeEvent = EventUtility.ParseEvent(jsonPayload, throwOnApiVersionMismatch: false);
            }
            else
            {
                stripeEvent = EventUtility.ConstructEvent(jsonPayload, signatureHeader, webhookSecret, throwOnApiVersionMismatch: false);
            }

            _logger.LogInformation("Processing Stripe Webhook Event: {EventType}", stripeEvent.Type);

            var type = stripeEvent.Type;
            if (type == PaymentConstants.StripeEvents.CheckoutSessionCompleted)
            {
                if (stripeEvent.Data.Object is Session session)
                {
                    var finalAmount = NormalizeStripeAmount(session.AmountTotal ?? 0, session.Currency);

                    var result = await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                         StripeSessionId: session.Id,
                         PaymentIntentId: !string.IsNullOrEmpty(session.InvoiceId) ? session.InvoiceId : session.PaymentIntentId,
                         Amount: finalAmount,
                         Currency: session.Currency,
                         UserIdStr: session.Metadata.ContainsKey(PaymentConstants.StripeMetadata.UserId) ? session.Metadata[PaymentConstants.StripeMetadata.UserId] : string.Empty,
                         WorkspaceIdStr: session.Metadata.ContainsKey(PaymentConstants.StripeMetadata.WorkspaceId) ? session.Metadata[PaymentConstants.StripeMetadata.WorkspaceId] : string.Empty,
                         PaymentType: session.Metadata.ContainsKey(PaymentConstants.StripeMetadata.PaymentType) ? session.Metadata[PaymentConstants.StripeMetadata.PaymentType] : string.Empty,
                         Status: PaymentConstants.PaymentStatuses.Paid,
                         PlanSlug: session.Metadata.ContainsKey(PaymentConstants.StripeMetadata.PlanSlug) ? session.Metadata[PaymentConstants.StripeMetadata.PlanSlug] : string.Empty,
                         BillingCycle: session.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? session.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty,
                        // WT-429: credits to grant, decided server-side at checkout creation.
                        Credits: session.Metadata.TryGetValue(PaymentConstants.StripeMetadata.Credits, out var sessionCredits)
                            && int.TryParse(sessionCredits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sessionCreditCount)
                            ? sessionCreditCount : 0,
                        // G11: an add-on checkout creates its own Stripe subscription.
                        // #466: so does a plan checkout with auto-renew on; the row is linked to it.
                        StripeSubscriptionId: session.SubscriptionId ?? string.Empty,
                        StripeCustomerId: session.CustomerId ?? string.Empty
                    ).WithCatalogMetadata(session.Metadata));
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
            }
            // WT-699 / TC3906: the buyer abandoned Checkout and Stripe expired the session. This
            // event used to fall through every branch unhandled, so a payment waiting on the
            // session stayed Pending forever. Same failure contract as every branch here — a
            // retryable failure is reported so Stripe redelivers.
            else if (type == PaymentConstants.StripeEvents.CheckoutSessionExpired)
            {
                if (stripeEvent.Data.Object is Session expiredSession)
                {
                    var result = await _paymentAppService.ExpireCheckoutSessionAsync(expiredSession.Id, cancellationToken);
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
            }
            else if (type == PaymentConstants.StripeEvents.PaymentIntentPaymentFailed)
            {
                if (stripeEvent.Data.Object is PaymentIntent intent)
                {
                    var result = await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: intent.Id,
                        Amount: NormalizeStripeAmount(intent.Amount, intent.Currency),
                        Currency: intent.Currency,
                        UserIdStr: intent.Metadata.ContainsKey(PaymentConstants.StripeMetadata.UserId) ? intent.Metadata[PaymentConstants.StripeMetadata.UserId] : string.Empty,
                        WorkspaceIdStr: intent.Metadata.ContainsKey(PaymentConstants.StripeMetadata.WorkspaceId) ? intent.Metadata[PaymentConstants.StripeMetadata.WorkspaceId] : string.Empty,
                        PaymentType: intent.Metadata.ContainsKey(PaymentConstants.StripeMetadata.PaymentType) ? intent.Metadata[PaymentConstants.StripeMetadata.PaymentType] : string.Empty,
                        Status: PaymentConstants.PaymentStatuses.Failed,
                        FailureReason: intent.LastPaymentError?.Message ?? PaymentConstants.StripePlaceholders.DefaultPaymentFailureReason,
                        PlanSlug: intent.Metadata.ContainsKey(PaymentConstants.StripeMetadata.PlanSlug) ? intent.Metadata[PaymentConstants.StripeMetadata.PlanSlug] : string.Empty,
                        BillingCycle: intent.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? intent.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty,
                        // WT-429: credits to grant, decided server-side at checkout creation.
                        Credits: intent.Metadata.TryGetValue(PaymentConstants.StripeMetadata.Credits, out var intentCredits)
                            && int.TryParse(intentCredits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intentCreditCount)
                            ? intentCreditCount : 0
                    ));
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
            }
            else if (type == PaymentConstants.StripeEvents.ChargeRefunded)
            {
                if (stripeEvent.Data.Object is Charge charge)
                {
                    var result = await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: charge.PaymentIntentId,
                        Amount: NormalizeStripeAmount(charge.AmountRefunded, charge.Currency),
                        Currency: charge.Currency,
                        UserIdStr: charge.Metadata.ContainsKey(PaymentConstants.StripeMetadata.UserId) ? charge.Metadata[PaymentConstants.StripeMetadata.UserId] : string.Empty,
                        WorkspaceIdStr: charge.Metadata.ContainsKey(PaymentConstants.StripeMetadata.WorkspaceId) ? charge.Metadata[PaymentConstants.StripeMetadata.WorkspaceId] : string.Empty,
                        PaymentType: charge.Metadata.ContainsKey(PaymentConstants.StripeMetadata.PaymentType) ? charge.Metadata[PaymentConstants.StripeMetadata.PaymentType] : string.Empty,
                        Status: PaymentConstants.PaymentStatuses.Refunded,
                        PlanSlug: charge.Metadata.ContainsKey(PaymentConstants.StripeMetadata.PlanSlug) ? charge.Metadata[PaymentConstants.StripeMetadata.PlanSlug] : string.Empty,
                        BillingCycle: charge.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? charge.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty,
                        // WT-429: credits to grant, decided server-side at checkout creation.
                        Credits: charge.Metadata.TryGetValue(PaymentConstants.StripeMetadata.Credits, out var chargeCredits)
                            && int.TryParse(chargeCredits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var chargeCreditCount)
                            ? chargeCreditCount : 0
                    ));
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
            }
            else if (type == PaymentConstants.StripeEvents.ChargeDisputeCreated)
            {
                if (stripeEvent.Data.Object is Dispute dispute)
                {
                    var result = await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: dispute.PaymentIntentId ?? dispute.ChargeId,
                        Amount: NormalizeStripeAmount(dispute.Amount, dispute.Currency),
                        Currency: dispute.Currency,
                        UserIdStr: string.Empty,
                        WorkspaceIdStr: string.Empty,
                        PaymentType: string.Empty,
                        Status: PaymentConstants.PaymentStatuses.Disputed
                    ));
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
            }
            else if (type == PaymentConstants.StripeEvents.CustomerSubscriptionUpdated)
            {
                if (stripeEvent.Data.Object is Stripe.Subscription addOnSubscription
                    && CatalogMetadataMapper.IsAddOnSubscription(addOnSubscription.Metadata))
                {
                    // G11: an add-on's own subscription. Never routed as a plan update.
                    var result = await _paymentAppService.ProcessPaymentEventAsync(AddOnEvent(
                        addOnSubscription, PaymentConstants.PaymentTypes.AddOnUpdate, PaymentConstants.PaymentStatuses.SubscriptionUpdated));
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
                else if (stripeEvent.Data.Object is Stripe.Subscription subscription)
                {
                    // #466: a plan subscription's renewal changed (auto-renew toggled here or in the
                    // Stripe portal, status moved). Mirrored onto the row; money never moves here.
                    var result = await ApplySubscriptionChangeAsync(subscription, deleted: false, cancellationToken);
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
            }
            else if (type == PaymentConstants.StripeEvents.CustomerSubscriptionDeleted)
            {
                if (stripeEvent.Data.Object is Stripe.Subscription addOnSubscription
                    && CatalogMetadataMapper.IsAddOnSubscription(addOnSubscription.Metadata))
                {
                    // G11: an add-on ending. Routed as a PLAN cancellation this would have ended
                    // the workspace's plan (CancellationPaymentEventHandler) — it must only end the add-on.
                    var result = await _paymentAppService.ProcessPaymentEventAsync(AddOnEvent(
                        addOnSubscription, PaymentConstants.PaymentTypes.AddOnCancellation, PaymentConstants.PaymentStatuses.Cancelled));
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
                else if (stripeEvent.Data.Object is Stripe.Subscription subscription)
                {
                    // #466: Stripe will never charge this subscription again. The plan ENDS AT
                    // PERIOD END — it used to be cancelled on the spot (status = cancelled took
                    // every entitlement away the moment the event arrived, mid-period).
                    var result = await ApplySubscriptionChangeAsync(subscription, deleted: true, cancellationToken);
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
            }
            else if (type == PaymentConstants.StripeEvents.InvoicePaid
                     || type == PaymentConstants.StripeEvents.InvoicePaymentFailed)
            {
                if (stripeEvent.Data.Object is Invoice invoice)
                {
                    var result = await HandleSubscriptionInvoiceAsync(invoice, paid: type == PaymentConstants.StripeEvents.InvoicePaid, cancellationToken);
                    if (result is { IsSuccess: false }) processingFailure = Capture(result, type);
                }
            }

            if (processingFailure is not null && IsWorthRedelivering(processingFailure.ErrorCode))
            {
                return Result.Failure<bool>(
                    processingFailure.Error ?? BillingMessageConstants.ApiErrorMessages.BillingPaymentEventFailed,
                    ErrorCodes.InternalServerError);
            }

            return Result.Success(true);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe exception occurred while handling webhook");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error occurred while handling webhook");
            return Result.Failure<bool>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// #466 — a subscription invoice was paid, or its charge failed.
    ///
    ///   * An add-on's renewal is the add-on's (G11), unchanged.
    ///   * A plan's FIRST invoice (billing_reason subscription_create) is the checkout itself, which
    ///     checkout.session.completed and the return page already activate. It used to be processed
    ///     as a second "Subscription" payment under the invoice id — a different idempotency key
    ///     from the session's — so a new subscriber was granted the plan's credits twice.
    ///   * A plan's CYCLE invoice is the renewal (paid) or the start of dunning (failed): one
    ///     SubscriptionRenewal payment event keyed by the invoice id, so a replay is a no-op and a
    ///     failed invoice that is later paid flips the same payment row.
    /// </summary>
    private async Task<Result?> HandleSubscriptionInvoiceAsync(Invoice invoice, bool paid, CancellationToken cancellationToken)
    {
        var reason = invoice.BillingReason;
        if (reason != InvoiceConstants.BillingReasons.SubscriptionCycle && reason != InvoiceConstants.BillingReasons.SubscriptionCreate)
        {
            return null;
        }

        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId
                             ?? invoice.Lines?.FirstOrDefault()?.SubscriptionId;
        if (string.IsNullOrEmpty(subscriptionId))
        {
            return null;
        }

        // The subscription's metadata as of the invoice rides on the invoice itself; the API is
        // asked only when it does not (older API versions).
        Dictionary<string, string>? metadata = invoice.Parent?.SubscriptionDetails?.Metadata;
        Stripe.Subscription? subscription = null;
        if (metadata is null || metadata.Count == 0)
        {
            subscription = await _stripeSubscriptionService.GetAsync(subscriptionId, cancellationToken: cancellationToken);
            metadata = subscription.Metadata;
        }

        metadata ??= new Dictionary<string, string>();
        var line = invoice.Lines?.FirstOrDefault();

        if (CatalogMetadataMapper.IsAddOnSubscription(metadata))
        {
            // G11: an add-on's invoice. The first one (subscription_create) is the checkout itself,
            // already recorded by checkout.session.completed; only a paid renewal is new money.
            if (!paid || reason != InvoiceConstants.BillingReasons.SubscriptionCycle)
            {
                return null;
            }

            subscription ??= await _stripeSubscriptionService.GetAsync(subscriptionId, cancellationToken: cancellationToken);
            return await _paymentAppService.ProcessPaymentEventAsync(AddOnEvent(
                subscription, PaymentConstants.PaymentTypes.AddOnRenewal, PaymentConstants.PaymentStatuses.Paid) with
            {
                PaymentIntentId = invoice.Id,
                Amount = NormalizeStripeAmount(paid ? invoice.AmountPaid : invoice.AmountDue, invoice.Currency),
                Currency = invoice.Currency,
                InvoiceUrl = invoice.HostedInvoiceUrl,
                InvoicePdf = invoice.InvoicePdf,
            });
        }

        if (reason != InvoiceConstants.BillingReasons.SubscriptionCycle)
        {
            _logger.LogInformation(
                "stripe_plan_first_invoice_skipped Invoice={InvoiceId} StripeSubscription={StripeSubscriptionId}: the checkout activates the plan.",
                invoice.Id, subscriptionId);
            return null;
        }

        string Meta(string key) => metadata.TryGetValue(key, out var value) ? value : string.Empty;

        return await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
            StripeSessionId: string.Empty,
            PaymentIntentId: invoice.Id,
            Amount: NormalizeStripeAmount(paid ? invoice.AmountPaid : invoice.AmountDue, invoice.Currency),
            Currency: invoice.Currency,
            UserIdStr: Meta(PaymentConstants.StripeMetadata.UserId),
            WorkspaceIdStr: Meta(PaymentConstants.StripeMetadata.WorkspaceId),
            PaymentType: PaymentConstants.PaymentTypes.SubscriptionRenewal,
            Status: paid ? PaymentConstants.PaymentStatuses.Paid : PaymentConstants.PaymentStatuses.Failed,
            FailureReason: paid
                ? string.Empty
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, "The card was declined for the renewal (attempt {0}).", invoice.AttemptCount),
            InvoiceUrl: invoice.HostedInvoiceUrl ?? string.Empty,
            InvoicePdf: invoice.InvoicePdf ?? string.Empty,
            PlanSlug: Meta(PaymentConstants.StripeMetadata.PlanSlug),
            BillingCycle: Meta(PaymentConstants.StripeMetadata.BillingCycle),
            StripeSubscriptionId: subscriptionId,
            PeriodEnd: line?.Period?.End,
            StripeCustomerId: invoice.CustomerId ?? string.Empty,
            PeriodStart: line?.Period?.Start));
    }

    /// <summary>#466: customer.subscription.updated / .deleted for a plan subscription.</summary>
    private async Task<Result> ApplySubscriptionChangeAsync(Stripe.Subscription subscription, bool deleted, CancellationToken cancellationToken)
    {
        if (_lifecycle is null)
        {
            return Result.Success();
        }

        return await _lifecycle.ApplyStripeSubscriptionChangeAsync(
            new StripeSubscriptionChange(
                subscription.Id,
                subscription.Status ?? string.Empty,
                subscription.CancelAtPeriodEnd,
                deleted,
                subscription.CustomerId,
                subscription.Metadata is not null && subscription.Metadata.TryGetValue(PaymentConstants.StripeMetadata.WorkspaceId, out var workspaceId)
                    ? workspaceId
                    : string.Empty,
                subscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd),
            cancellationToken);
    }

    /// <summary>
    /// Logs a failed payment event and hands it back so the caller can decide the HTTP answer.
    ///
    /// Error, not Warning: a payment has been taken and the workspace did not get what it paid
    /// for. The message keeps its original opening words on purpose — "Webhook payment processing
    /// failed" is the string already written down as the one to grep for.
    /// </summary>
    private Result Capture(Result result, string eventType)
    {
        _logger.LogError(
            "Webhook payment processing failed: {Error} (EventType: {EventType}, ErrorCode: {ErrorCode})",
            result.Error,
            eventType,
            result.ErrorCode);
        return result;
    }

    /// <summary>
    /// Would sending this exact event again have a different outcome?
    ///
    /// A validation error means the payload cannot be used — the workspace id does not parse, the
    /// plan slug names nothing. Three days of identical redeliveries produce three days of
    /// identical failures, so those are acknowledged and left to the log. Everything else is
    /// treated as transient (a database, a network, a service that was briefly unavailable),
    /// which is precisely the case Stripe's redelivery schedule exists to rescue.
    /// </summary>
    private static bool IsWorthRedelivering(string? errorCode) =>
        errorCode != ErrorCodes.ValidationError
        && errorCode != ErrorCodes.BillingPlanNotFound
        && errorCode != ErrorCodes.NotFound;

    /// <summary>
    /// G11: a payment event about an add-on's Stripe subscription. Keyed on the subscription id
    /// (not a session), and carrying the live quantity, cancel flag and paid-through date.
    /// </summary>
    private static StripePaymentEventRequest AddOnEvent(Stripe.Subscription subscription, string paymentType, string status)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();
        string Meta(string key) => subscription.Metadata.TryGetValue(key, out var value) ? value : string.Empty;

        return new StripePaymentEventRequest(
            StripeSessionId: string.Empty,
            PaymentIntentId: subscription.Id,
            Amount: 0,
            Currency: subscription.Currency ?? PaymentConstants.Currencies.Usd,
            UserIdStr: Meta(PaymentConstants.StripeMetadata.UserId),
            WorkspaceIdStr: Meta(PaymentConstants.StripeMetadata.WorkspaceId),
            PaymentType: paymentType,
            Status: status,
            BillingCycle: Meta(PaymentConstants.StripeMetadata.BillingCycle),
            Quantity: (int)(item?.Quantity ?? 0),
            StripeSubscriptionId: subscription.Id,
            CancelAtPeriodEnd: subscription.CancelAtPeriodEnd,
            PeriodEnd: item?.CurrentPeriodEnd
        ).WithCatalogMetadata(subscription.Metadata);
    }

    private static decimal NormalizeStripeAmount(decimal amount, string? currency)
    {
        return string.Equals(currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
            ? amount
            : amount / 100m;
    }
}
