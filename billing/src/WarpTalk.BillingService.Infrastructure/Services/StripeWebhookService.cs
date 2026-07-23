using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;

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

    public async Task<bool> HandleWebhookAsync(string jsonPayload, string signatureHeader)
    {
        try
        {
            var webhookSecret = _configuration["Stripe:WebhookSecret"];
            Event stripeEvent;

            if (string.IsNullOrEmpty(webhookSecret) || webhookSecret == "whsec_test_secret")
            {
                if (!_environment.IsDevelopment())
                {
                    _logger.LogError("Stripe webhook secret is not configured in production.");
                    return false;
                }

                stripeEvent = EventUtility.ParseEvent(jsonPayload, throwOnApiVersionMismatch: false);
            }
            else
            {
                stripeEvent = EventUtility.ConstructEvent(jsonPayload, signatureHeader, webhookSecret, throwOnApiVersionMismatch: false);
            }

            _logger.LogInformation("Processing Stripe Webhook Event: {EventType}", stripeEvent.Type);

            var type = stripeEvent.Type;
            if (type == "checkout.session.completed")
            {
                if (stripeEvent.Data.Object is Session session)
                {
                    var isZeroDecimal = string.Equals(session.Currency, "vnd", StringComparison.OrdinalIgnoreCase);
                    var finalAmount = isZeroDecimal ? (session.AmountTotal ?? 0) : ((session.AmountTotal ?? 0) / 100m);

                    await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: session.Id,
                        PaymentIntentId: !string.IsNullOrEmpty(session.InvoiceId) ? session.InvoiceId : session.PaymentIntentId,
                        Amount: finalAmount,
                        Currency: session.Currency,
                        UserIdStr: session.Metadata.ContainsKey("UserId") ? session.Metadata["UserId"] : string.Empty,
                        WorkspaceIdStr: session.Metadata.ContainsKey("WorkspaceId") ? session.Metadata["WorkspaceId"] : string.Empty,
                        PaymentType: session.Metadata.ContainsKey("PaymentType") ? session.Metadata["PaymentType"] : string.Empty,
                        Status: "paid",
                        PlanSlug: session.Metadata.ContainsKey("PlanSlug") ? session.Metadata["PlanSlug"] : string.Empty,
                        BillingCycle: session.Metadata.ContainsKey("BillingCycle") ? session.Metadata["BillingCycle"] : string.Empty
                    ));
                }
            }
            else if (type == "payment_intent.payment_failed")
            {
                if (stripeEvent.Data.Object is PaymentIntent intent)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: intent.Id,
                        Amount: intent.Amount / 100m,
                        Currency: intent.Currency,
                        UserIdStr: intent.Metadata.ContainsKey("UserId") ? intent.Metadata["UserId"] : string.Empty,
                        WorkspaceIdStr: intent.Metadata.ContainsKey("WorkspaceId") ? intent.Metadata["WorkspaceId"] : string.Empty,
                        PaymentType: intent.Metadata.ContainsKey("PaymentType") ? intent.Metadata["PaymentType"] : string.Empty,
                        Status: "failed",
                        FailureReason: intent.LastPaymentError?.Message ?? "Payment failed",
                        PlanSlug: intent.Metadata.ContainsKey("PlanSlug") ? intent.Metadata["PlanSlug"] : string.Empty,
                        BillingCycle: intent.Metadata.ContainsKey("BillingCycle") ? intent.Metadata["BillingCycle"] : string.Empty
                    ));
                }
            }
            else if (type == "charge.refunded")
            {
                if (stripeEvent.Data.Object is Charge charge)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: charge.PaymentIntentId,
                        Amount: charge.AmountRefunded / 100m,
                        Currency: charge.Currency,
                        UserIdStr: charge.Metadata.ContainsKey("UserId") ? charge.Metadata["UserId"] : string.Empty,
                        WorkspaceIdStr: charge.Metadata.ContainsKey("WorkspaceId") ? charge.Metadata["WorkspaceId"] : string.Empty,
                        PaymentType: charge.Metadata.ContainsKey("PaymentType") ? charge.Metadata["PaymentType"] : string.Empty,
                        Status: "refunded",
                        PlanSlug: charge.Metadata.ContainsKey("PlanSlug") ? charge.Metadata["PlanSlug"] : string.Empty,
                        BillingCycle: charge.Metadata.ContainsKey("BillingCycle") ? charge.Metadata["BillingCycle"] : string.Empty
                    ));
                }
            }
            else if (type == "charge.dispute.created")
            {
                if (stripeEvent.Data.Object is Dispute dispute)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: dispute.PaymentIntentId ?? dispute.ChargeId,
                        Amount: dispute.Amount / 100m,
                        Currency: dispute.Currency,
                        UserIdStr: string.Empty,
                        WorkspaceIdStr: string.Empty,
                        PaymentType: string.Empty,
                        Status: "disputed"
                    ));
                }
            }
            else if (type == "customer.subscription.updated")
            {
                if (stripeEvent.Data.Object is Stripe.Subscription subscription)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: subscription.Id,
                        Amount: subscription.Items.Data.FirstOrDefault()?.Price.UnitAmountDecimal / 100m ?? 0,
                        Currency: subscription.Currency ?? "usd",
                        UserIdStr: subscription.Metadata.ContainsKey("UserId") ? subscription.Metadata["UserId"] : string.Empty,
                        WorkspaceIdStr: subscription.Metadata.ContainsKey("WorkspaceId") ? subscription.Metadata["WorkspaceId"] : string.Empty,
                        PaymentType: "SubscriptionUpdate",
                        Status: "subscription_updated",
                        PlanSlug: subscription.Metadata.ContainsKey("PlanSlug") ? subscription.Metadata["PlanSlug"] : string.Empty,
                        BillingCycle: subscription.Metadata.ContainsKey("BillingCycle") ? subscription.Metadata["BillingCycle"] : string.Empty
                    ));
                }
            }
            else if (type == "customer.subscription.deleted")
            {
                if (stripeEvent.Data.Object is Stripe.Subscription subscription)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                        StripeSessionId: string.Empty,
                        PaymentIntentId: subscription.Id,
                        Amount: 0,
                        Currency: "usd",
                        UserIdStr: subscription.Metadata.ContainsKey("UserId") ? subscription.Metadata["UserId"] : string.Empty,
                        WorkspaceIdStr: subscription.Metadata.ContainsKey("WorkspaceId") ? subscription.Metadata["WorkspaceId"] : string.Empty,
                        PaymentType: "Subscription",
                        Status: "cancelled",
                        PlanSlug: subscription.Metadata.ContainsKey("PlanSlug") ? subscription.Metadata["PlanSlug"] : string.Empty,
                        BillingCycle: subscription.Metadata.ContainsKey("BillingCycle") ? subscription.Metadata["BillingCycle"] : string.Empty
                    ));
                }
            }
            else if (type == "invoice.paid")
            {
                if (stripeEvent.Data.Object is Invoice invoice && (invoice.BillingReason == "subscription_cycle" || invoice.BillingReason == "subscription_create"))
                {
                    var subId = invoice.Lines?.FirstOrDefault()?.SubscriptionId;
                    if (!string.IsNullOrEmpty(subId))
                    {
                        var subscription = await _stripeSubscriptionService.GetAsync(subId);

                        string paymentType = invoice.BillingReason == "subscription_create" ? "Subscription" : "SubscriptionRenewal";
                        var isZeroDecimal = string.Equals(invoice.Currency, "vnd", StringComparison.OrdinalIgnoreCase);
                        var finalAmount = isZeroDecimal ? (decimal)invoice.AmountPaid : ((decimal)invoice.AmountPaid / 100m);

                        await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                            StripeSessionId: string.Empty,
                            PaymentIntentId: invoice.Id,
                            Amount: finalAmount,
                            Currency: invoice.Currency,
                            UserIdStr: subscription.Metadata.ContainsKey("UserId") ? subscription.Metadata["UserId"] : string.Empty,
                            WorkspaceIdStr: subscription.Metadata.ContainsKey("WorkspaceId") ? subscription.Metadata["WorkspaceId"] : string.Empty,
                            PaymentType: paymentType,
                            Status: "paid",
                            InvoiceUrl: invoice.HostedInvoiceUrl,
                            InvoicePdf: invoice.InvoicePdf,
                            PlanSlug: subscription.Metadata.ContainsKey("PlanSlug") ? subscription.Metadata["PlanSlug"] : string.Empty,
                            BillingCycle: subscription.Metadata.ContainsKey("BillingCycle") ? subscription.Metadata["BillingCycle"] : string.Empty
                        ));
                    }
                }
            }

            return true;
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe exception occurred while handling webhook");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error occurred while handling webhook");
            return false;
        }
    }
}
