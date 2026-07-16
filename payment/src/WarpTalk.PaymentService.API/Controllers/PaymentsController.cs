using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
    private readonly IHostEnvironment _environment;

    public PaymentsController(IPaymentAppService paymentAppService, IConfiguration configuration, IHostEnvironment environment)
    {
        _paymentAppService = paymentAppService;
        _configuration = configuration;
        _environment = environment;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateCheckoutSessionRequest request)
    {
        Console.WriteLine($"[PAYMENTS-DIAGNOSTIC] Received checkout request: Amount={request.Amount}, Currency='{request.Currency}', PaymentType='{request.PaymentType}', WorkspaceId={request.WorkspaceId}");
        var url = await _paymentAppService.CreateCheckoutSessionAsync(request);
        return Ok(new { url });
    }

    [HttpGet("checkout-session/{sessionId}")]
    public async Task<IActionResult> GetCheckoutSession(string sessionId)
    {
        try
        {
            Stripe.Checkout.Session session;
            if (sessionId.StartsWith("mock_session_"))
            {
                var payloadBase64 = sessionId.Substring("mock_session_".Length);
                var payloadJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
                var payload = System.Text.Json.JsonSerializer.Deserialize<MockSessionPayload>(payloadJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (payload == null)
                {
                    return BadRequest("Invalid mock session payload.");
                }

                session = new Stripe.Checkout.Session
                {
                    Id = sessionId,
                    AmountTotal = (long)(string.Equals(payload.Currency, "vnd", StringComparison.OrdinalIgnoreCase) ? payload.Amount : payload.Amount * 100),
                    Currency = payload.Currency,
                    PaymentStatus = "paid",
                    Status = "complete",
                    PaymentIntentId = "mock_pi_" + Guid.NewGuid().ToString("N"),
                    Metadata = new Dictionary<string, string>
                    {
                        { "UserId", payload.UserId.ToString() },
                        { "WorkspaceId", payload.WorkspaceId.ToString() },
                        { "PaymentType", payload.PaymentType },
                        { "PlanSlug", payload.PlanSlug ?? "" },
                        { "BillingCycle", payload.BillingCycle ?? "" }
                    }
                };
            }
            else
            {
                var service = new Stripe.Checkout.SessionService();
                session = await service.GetAsync(sessionId);
            }
            
            if (session == null)
            {
                return NotFound("Session not found.");
            }
            
            if (session.PaymentStatus == "paid")
            {
                Console.WriteLine($"[PAYMENTS-LOCAL-FALLBACK] Processing checkout session {session.Id} directly via success page API call");
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
                    session.Metadata.ContainsKey("PlanSlug") ? session.Metadata["PlanSlug"] : string.Empty,
                    session.Metadata.ContainsKey("BillingCycle") ? session.Metadata["BillingCycle"] : string.Empty
                );
            }

            return Ok(new {
                id = session.Id,
                amountTotal = session.AmountTotal,
                currency = session.Currency,
                metadata = session.Metadata,
                paymentStatus = session.PaymentStatus,
                status = session.Status,
                paymentIntentId = session.PaymentIntentId
            });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
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
                if (!_environment.IsDevelopment())
                {
                    return StatusCode(500, "Stripe webhook secret is not configured.");
                }

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
                        session.Metadata.ContainsKey("PlanSlug") ? session.Metadata["PlanSlug"] : string.Empty,
                        session.Metadata.ContainsKey("BillingCycle") ? session.Metadata["BillingCycle"] : string.Empty
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
                        intent.LastPaymentError?.Message ?? "Payment failed",
                        "",
                        "",
                        intent.Metadata.ContainsKey("PlanSlug") ? intent.Metadata["PlanSlug"] : string.Empty,
                        intent.Metadata.ContainsKey("BillingCycle") ? intent.Metadata["BillingCycle"] : string.Empty
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
                        "refunded",
                        "",
                        "",
                        charge.Metadata.ContainsKey("PlanSlug") ? charge.Metadata["PlanSlug"] : string.Empty,
                        charge.Metadata.ContainsKey("BillingCycle") ? charge.Metadata["BillingCycle"] : string.Empty
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
                        "disputed",
                        "",
                        "",
                        string.Empty,
                        string.Empty
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
                        "cancelled",
                        "",
                        "",
                        subscription.Metadata.ContainsKey("PlanSlug") ? subscription.Metadata["PlanSlug"] : string.Empty,
                        subscription.Metadata.ContainsKey("BillingCycle") ? subscription.Metadata["BillingCycle"] : string.Empty
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
                        var isZeroDecimal = string.Equals(invoice.Currency, "vnd", StringComparison.OrdinalIgnoreCase);
                        var finalAmount = isZeroDecimal ? (decimal)invoice.AmountPaid : ((decimal)invoice.AmountPaid / 100m);

                        await _paymentAppService.ProcessPaymentEventAsync(
                            string.Empty,
                            invoice.Id, // Use invoice.Id as ProviderTransactionId
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

    private class MockSessionPayload
    {
        public Guid UserId { get; set; }
        public Guid WorkspaceId { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string PaymentType { get; set; } = string.Empty;
        public string? PlanSlug { get; set; }
        public string? BillingCycle { get; set; }
    }
}
