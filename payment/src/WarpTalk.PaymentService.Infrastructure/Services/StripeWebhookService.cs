using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using WarpTalk.PaymentService.Application.Interfaces;

namespace WarpTalk.PaymentService.Infrastructure.Services;

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

            switch (stripeEvent.Type)
            {
                case "checkout.session.completed":
                    if (stripeEvent.Data.Object is Session session)
                    {
                        var isZeroDecimal = string.Equals(session.Currency, "vnd", StringComparison.OrdinalIgnoreCase);
                        var finalAmount = isZeroDecimal ? (session.AmountTotal ?? 0) : ((session.AmountTotal ?? 0) / 100m);

                        await _paymentAppService.ProcessPaymentEventAsync(
                            session.Id,
                            !string.IsNullOrEmpty(session.InvoiceId) ? session.InvoiceId : session.PaymentIntentId,
                            finalAmount,
                            session.Currency,
                            session.Metadata.ContainsKey("UserId") ? session.Metadata["UserId"] : string.Empty,
                            session.Metadata.ContainsKey("WorkspaceId") ? session.Metadata["WorkspaceId"] : string.Empty,
                            session.Metadata.ContainsKey("PaymentType") ? session.Metadata["PaymentType"] : string.Empty,
                            "paid",
                            "",
                            "",
                            "",
                            session.Metadata.ContainsKey("PlanSlug") ? session.Metadata["PlanSlug"] : string.Empty,
                            session.Metadata.ContainsKey("BillingCycle") ? session.Metadata["BillingCycle"] : string.Empty
                        );
                    }
                    break;

                case "payment_intent.payment_failed":
                    if (stripeEvent.Data.Object is PaymentIntent intent)
                    {
                        await _paymentAppService.ProcessPaymentEventAsync(
                            string.Empty,
                            intent.Id,
                            intent.Amount / 100m,
                            intent.Currency,
                            intent.Metadata.ContainsKey("UserId") ? intent.Metadata["UserId"] : string.Empty,
                            intent.Metadata.ContainsKey("WorkspaceId") ? intent.Metadata["WorkspaceId"] : string.Empty,
                            intent.Metadata.ContainsKey("PaymentType") ? intent.Metadata["PaymentType"] : string.Empty,
                            "failed",
                            intent.LastPaymentError?.Message ?? "Payment failed",
                            "",
                            "",
                            intent.Metadata.ContainsKey("PlanSlug") ? intent.Metadata["PlanSlug"] : string.Empty,
                            intent.Metadata.ContainsKey("BillingCycle") ? intent.Metadata["BillingCycle"] : string.Empty
                        );
                    }
                    break;

                case "charge.refunded":
                    if (stripeEvent.Data.Object is Charge charge)
                    {
                        await _paymentAppService.ProcessPaymentEventAsync(
                            string.Empty,
                            charge.PaymentIntentId,
                            charge.AmountRefunded / 100m,
                            charge.Currency,
                            charge.Metadata.ContainsKey("UserId") ? charge.Metadata["UserId"] : string.Empty,
                            charge.Metadata.ContainsKey("WorkspaceId") ? charge.Metadata["WorkspaceId"] : string.Empty,
                            charge.Metadata.ContainsKey("PaymentType") ? charge.Metadata["PaymentType"] : string.Empty,
                            "refunded",
                            "",
                            "",
                            "",
                            charge.Metadata.ContainsKey("PlanSlug") ? charge.Metadata["PlanSlug"] : string.Empty,
                            charge.Metadata.ContainsKey("BillingCycle") ? charge.Metadata["BillingCycle"] : string.Empty
                        );
                    }
                    break;

                case "charge.dispute.created":
                    if (stripeEvent.Data.Object is Dispute dispute)
                    {
                        await _paymentAppService.ProcessPaymentEventAsync(
                            string.Empty,
                            dispute.PaymentIntentId ?? dispute.ChargeId,
                            dispute.Amount / 100m,
                            dispute.Currency,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            "disputed",
                            "",
                            "",
                            "",
                            string.Empty,
                            string.Empty
                        );
                    }
                    break;

                case "customer.subscription.deleted":
                    {
                        if (stripeEvent.Data.Object is Stripe.Subscription subscription)
                        {
                            await _paymentAppService.ProcessPaymentEventAsync(
                                string.Empty,
                                subscription.Id,
                                0,
                                "usd",
                                subscription.Metadata.ContainsKey("UserId") ? subscription.Metadata["UserId"] : string.Empty,
                                subscription.Metadata.ContainsKey("WorkspaceId") ? subscription.Metadata["WorkspaceId"] : string.Empty,
                                "Subscription",
                                "cancelled",
                                "",
                                "",
                                "",
                                subscription.Metadata.ContainsKey("PlanSlug") ? subscription.Metadata["PlanSlug"] : string.Empty,
                                subscription.Metadata.ContainsKey("BillingCycle") ? subscription.Metadata["BillingCycle"] : string.Empty
                            );
                        }
                    }
                    break;

                case "invoice.paid":
                    if (stripeEvent.Data.Object is Invoice invoice && (invoice.BillingReason == "subscription_cycle" || invoice.BillingReason == "subscription_create"))
                    {
                        var subId = invoice.Lines?.FirstOrDefault()?.SubscriptionId;
                        if (!string.IsNullOrEmpty(subId))
                        {
                            var subscription = await _stripeSubscriptionService.GetAsync(subId);

                            string paymentType = invoice.BillingReason == "subscription_create" ? "Subscription" : "SubscriptionRenewal";
                            var isZeroDecimal = string.Equals(invoice.Currency, "vnd", StringComparison.OrdinalIgnoreCase);
                            var finalAmount = isZeroDecimal ? (decimal)invoice.AmountPaid : ((decimal)invoice.AmountPaid / 100m);

                            await _paymentAppService.ProcessPaymentEventAsync(
                                string.Empty,
                                invoice.Id,
                                finalAmount,
                                invoice.Currency,
                                subscription.Metadata.ContainsKey("UserId") ? subscription.Metadata["UserId"] : string.Empty,
                                subscription.Metadata.ContainsKey("WorkspaceId") ? subscription.Metadata["WorkspaceId"] : string.Empty,
                                paymentType,
                                "paid",
                                "",
                                invoice.HostedInvoiceUrl,
                                invoice.InvoicePdf,
                                subscription.Metadata.ContainsKey("PlanSlug") ? subscription.Metadata["PlanSlug"] : string.Empty,
                                subscription.Metadata.ContainsKey("BillingCycle") ? subscription.Metadata["BillingCycle"] : string.Empty
                            );
                        }
                    }
                    break;
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
