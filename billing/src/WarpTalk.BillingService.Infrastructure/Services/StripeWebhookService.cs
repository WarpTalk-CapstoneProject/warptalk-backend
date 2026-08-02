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
                         BillingCycle: session.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? session.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty
                    ));
                    if (!result.IsSuccess) _logger.LogWarning("Webhook payment processing failed: {Error}", result.Error);
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
                        BillingCycle: intent.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? intent.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty
                    ));
                    if (!result.IsSuccess) _logger.LogWarning("Webhook payment processing failed: {Error}", result.Error);
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
                        BillingCycle: charge.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? charge.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty
                    ));
                    if (!result.IsSuccess) _logger.LogWarning("Webhook payment processing failed: {Error}", result.Error);
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
                    if (!result.IsSuccess) _logger.LogWarning("Webhook payment processing failed: {Error}", result.Error);
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
                        BillingCycle: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? subscription.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty
                    ));
                    if (!result.IsSuccess) _logger.LogWarning("Webhook payment processing failed: {Error}", result.Error);
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
                        BillingCycle: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? subscription.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty
                    ));
                    if (!result.IsSuccess) _logger.LogWarning("Webhook payment processing failed: {Error}", result.Error);
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
                            BillingCycle: subscription.Metadata.ContainsKey(PaymentConstants.StripeMetadata.BillingCycle) ? subscription.Metadata[PaymentConstants.StripeMetadata.BillingCycle] : string.Empty
                        ));
                        if (!result.IsSuccess) _logger.LogWarning("Webhook payment processing failed: {Error}", result.Error);
                    }
                }
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

    private static decimal NormalizeStripeAmount(decimal amount, string? currency)
    {
        return string.Equals(currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
            ? amount
            : amount / 100m;
    }
}
