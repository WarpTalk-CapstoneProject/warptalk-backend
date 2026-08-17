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

    public StripeWebhookService(
        IPaymentAppService paymentAppService,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<StripeWebhookService> logger,
        Stripe.SubscriptionService stripeSubscriptionService)
    {
        _paymentAppService = paymentAppService;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
        _stripeSubscriptionService = stripeSubscriptionService;
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
                            ? sessionCreditCount : 0
                    ));
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
                if (stripeEvent.Data.Object is Stripe.Subscription subscription)
                {
                    var result = await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: subscription.Id,
                        Amount: NormalizeStripeAmount(subscription.Items.Data.FirstOrDefault()?.Price.UnitAmountDecimal ?? 0, subscription.Currency),
                        Currency: subscription.Currency ?? PaymentConstants.Currencies.Usd,
                        UserIdStr: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.UserId) ? subscription.Metadata[PaymentConstants.StripeMetadata.UserId] : string.Empty,
                        WorkspaceIdStr: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.WorkspaceId) ? subscription.Metadata[PaymentConstants.StripeMetadata.WorkspaceId] : string.Empty,
                        PaymentType: PaymentConstants.PaymentTypes.SubscriptionUpdate,
                        Status: PaymentConstants.PaymentStatuses.SubscriptionUpdated,
                        PlanSlug: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.PlanSlug) ? subscription.Metadata[PaymentConstants.StripeMetadata.PlanSlug] : string.Empty,
                        BillingCycle: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? subscription.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty,
                        // WT-429: credits to grant, decided server-side at checkout creation.
                        Credits: subscription.Metadata.TryGetValue(PaymentConstants.StripeMetadata.Credits, out var subscriptionCredits)
                            && int.TryParse(subscriptionCredits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var subscriptionCreditCount)
                            ? subscriptionCreditCount : 0
                    ));
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
            }
            else if (type == PaymentConstants.StripeEvents.CustomerSubscriptionDeleted)
            {
                if (stripeEvent.Data.Object is Stripe.Subscription subscription)
                {
                    var result = await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: subscription.Id,
                        Amount: 0,
                        Currency: PaymentConstants.Currencies.Usd,
                        UserIdStr: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.UserId) ? subscription.Metadata[PaymentConstants.StripeMetadata.UserId] : string.Empty,
                        WorkspaceIdStr: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.WorkspaceId) ? subscription.Metadata[PaymentConstants.StripeMetadata.WorkspaceId] : string.Empty,
                        PaymentType: PaymentConstants.PaymentTypes.Subscription,
                        Status: PaymentConstants.PaymentStatuses.Cancelled,
                        PlanSlug: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.PlanSlug) ? subscription.Metadata[PaymentConstants.StripeMetadata.PlanSlug] : string.Empty,
                        BillingCycle: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? subscription.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty,
                        // WT-429: credits to grant, decided server-side at checkout creation.
                        Credits: subscription.Metadata.TryGetValue(PaymentConstants.StripeMetadata.Credits, out var subscriptionCredits)
                            && int.TryParse(subscriptionCredits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var subscriptionCreditCount)
                            ? subscriptionCreditCount : 0
                    ));
                    if (!result.IsSuccess) processingFailure = Capture(result, type);
                }
            }
            else if (type == PaymentConstants.StripeEvents.InvoicePaid)
            {
                if (stripeEvent.Data.Object is Invoice invoice && (invoice.BillingReason == InvoiceConstants.BillingReasons.SubscriptionCycle || invoice.BillingReason == InvoiceConstants.BillingReasons.SubscriptionCreate))
                {
                    var subId = invoice.Lines?.FirstOrDefault()?.SubscriptionId;
                    if (!string.IsNullOrEmpty(subId))
                    {
                        var subscription = await _stripeSubscriptionService.GetAsync(subId);

                        string paymentType = invoice.BillingReason == InvoiceConstants.BillingReasons.SubscriptionCreate ? PaymentConstants.PaymentTypes.Subscription : PaymentConstants.PaymentTypes.SubscriptionRenewal;
                        var finalAmount = NormalizeStripeAmount(invoice.AmountPaid, invoice.Currency);

                        var result = await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                            StripeSessionId: string.Empty,
                            PaymentIntentId: invoice.Id,
                            Amount: finalAmount,
                            Currency: invoice.Currency,
                            UserIdStr: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.UserId) ? subscription.Metadata[PaymentConstants.StripeMetadata.UserId] : string.Empty,
                            WorkspaceIdStr: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.WorkspaceId) ? subscription.Metadata[PaymentConstants.StripeMetadata.WorkspaceId] : string.Empty,
                            PaymentType: paymentType,
                            Status: PaymentConstants.PaymentStatuses.Paid,
                            InvoiceUrl: invoice.HostedInvoiceUrl,
                            InvoicePdf: invoice.InvoicePdf,
                            PlanSlug: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.PlanSlug) ? subscription.Metadata[PaymentConstants.StripeMetadata.PlanSlug] : string.Empty,
                            BillingCycle: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? subscription.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty,
                        // WT-429: credits to grant, decided server-side at checkout creation.
                        Credits: subscription.Metadata.TryGetValue(PaymentConstants.StripeMetadata.Credits, out var subscriptionCredits)
                            && int.TryParse(subscriptionCredits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var subscriptionCreditCount)
                            ? subscriptionCreditCount : 0
                        ));
                        if (!result.IsSuccess) processingFailure = Capture(result, type);
                    }
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

    private static decimal NormalizeStripeAmount(decimal amount, string? currency)
    {
        return string.Equals(currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
            ? amount
            : amount / 100m;
    }
}
