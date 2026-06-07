using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Stripe;
using WarpTalk.PaymentService.Application.DTOs;
using WarpTalk.PaymentService.Application.Interfaces;

namespace WarpTalk.PaymentService.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentAppService _paymentAppService;
    private readonly IConfiguration _configuration;

    public PaymentsController(IPaymentAppService paymentAppService, IConfiguration configuration)
    {
        _paymentAppService = paymentAppService;
        _configuration = configuration;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateCheckoutSessionRequest request)
    {
        var url = await _paymentAppService.CreateCheckoutSessionAsync(request);
        return Ok(new { url });
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        try
        {
            var webhookSecret = _configuration["Stripe:WebhookSecret"];
            Event stripeEvent;
            if (string.IsNullOrEmpty(webhookSecret) || webhookSecret == "whsec_test_secret")
            {
                stripeEvent = EventUtility.ParseEvent(json, throwOnApiVersionMismatch: false);
            }
            else
            {
                stripeEvent = EventUtility.ConstructEvent(json, Request.Headers["Stripe-Signature"], webhookSecret, throwOnApiVersionMismatch: false);
            }

            if (stripeEvent.Type == "checkout.session.completed")
            {
                var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
                if (session != null)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(
                        session.Id,
                        !string.IsNullOrEmpty(session.InvoiceId) ? session.InvoiceId : session.PaymentIntentId,
                        (session.AmountTotal ?? 0) / 100m,
                        session.Currency,
                        session.Metadata.ContainsKey("UserId") ? session.Metadata["UserId"] : string.Empty,
                        session.Metadata.ContainsKey("WorkspaceId") ? session.Metadata["WorkspaceId"] : string.Empty,
                        session.Metadata.ContainsKey("PaymentType") ? session.Metadata["PaymentType"] : string.Empty,
                        "paid"
                    );
                }
            }
            else if (stripeEvent.Type == "payment_intent.payment_failed")
            {
                var intent = stripeEvent.Data.Object as Stripe.PaymentIntent;
                if (intent != null)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(
                        string.Empty, // Session ID might not be readily available
                        intent.Id,
                        intent.Amount / 100m,
                        intent.Currency,
                        intent.Metadata.ContainsKey("UserId") ? intent.Metadata["UserId"] : string.Empty,
                        intent.Metadata.ContainsKey("WorkspaceId") ? intent.Metadata["WorkspaceId"] : string.Empty,
                        intent.Metadata.ContainsKey("PaymentType") ? intent.Metadata["PaymentType"] : string.Empty,
                        "failed",
                        intent.LastPaymentError?.Message ?? "Payment failed"
                    );
                }
            }
            else if (stripeEvent.Type == "charge.refunded")
            {
                var charge = stripeEvent.Data.Object as Stripe.Charge;
                if (charge != null)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(
                        string.Empty,
                        charge.PaymentIntentId,
                        charge.AmountRefunded / 100m,
                        charge.Currency,
                        charge.Metadata.ContainsKey("UserId") ? charge.Metadata["UserId"] : string.Empty,
                        charge.Metadata.ContainsKey("WorkspaceId") ? charge.Metadata["WorkspaceId"] : string.Empty,
                        charge.Metadata.ContainsKey("PaymentType") ? charge.Metadata["PaymentType"] : string.Empty,
                        "refunded"
                    );
                }
            }
            else if (stripeEvent.Type == "charge.dispute.created")
            {
                var dispute = stripeEvent.Data.Object as Stripe.Dispute;
                if (dispute != null)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(
                        string.Empty,
                        dispute.PaymentIntentId ?? dispute.ChargeId,
                        dispute.Amount / 100m,
                        dispute.Currency,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        "disputed"
                    );
                }
            }
            else if (stripeEvent.Type == "customer.subscription.deleted")
            {
                var subscription = stripeEvent.Data.Object as Stripe.Subscription;
                if (subscription != null)
                {
                    await _paymentAppService.ProcessPaymentEventAsync(
                        string.Empty,
                        subscription.Id,
                        0,
                        "usd",
                        subscription.Metadata.ContainsKey("UserId") ? subscription.Metadata["UserId"] : string.Empty,
                        subscription.Metadata.ContainsKey("WorkspaceId") ? subscription.Metadata["WorkspaceId"] : string.Empty,
                        "Subscription",
                        "cancelled"
                    );
                }
            }
            else if (stripeEvent.Type == "invoice.paid")
            {
                var invoice = stripeEvent.Data.Object as Stripe.Invoice;
                if (invoice != null && (invoice.BillingReason == "subscription_cycle" || invoice.BillingReason == "subscription_create"))
                {
                    var subId = invoice.Lines?.FirstOrDefault()?.SubscriptionId;
                    if (!string.IsNullOrEmpty(subId))
                    {
                        var subscriptionService = new SubscriptionService();
                        var subscription = await subscriptionService.GetAsync(subId);

                        string paymentType = invoice.BillingReason == "subscription_create" ? "Subscription" : "SubscriptionRenewal";

                        await _paymentAppService.ProcessPaymentEventAsync(
                            string.Empty,
                            invoice.Id, // Use invoice.Id as ProviderTransactionId
                            invoice.AmountPaid / 100m,
                            invoice.Currency,
                            subscription.Metadata.ContainsKey("UserId") ? subscription.Metadata["UserId"] : string.Empty,
                            subscription.Metadata.ContainsKey("WorkspaceId") ? subscription.Metadata["WorkspaceId"] : string.Empty,
                            paymentType,
                            "paid",
                            "",
                            invoice.HostedInvoiceUrl,
                            invoice.InvoicePdf
                        );
                    }
                }
            }

            return Ok();
        }
        catch (StripeException ex)
        {
            Console.WriteLine($"StripeException: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex.Message}");
            return StatusCode(500, ex.Message);
        }
    }
}
