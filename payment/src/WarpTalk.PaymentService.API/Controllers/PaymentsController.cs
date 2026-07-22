using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WarpTalk.PaymentService.Application.DTOs;
using WarpTalk.PaymentService.Application.Interfaces;

namespace WarpTalk.PaymentService.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentAppService _paymentAppService;
    private readonly IStripeWebhookService _stripeWebhookService;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        IPaymentAppService paymentAppService,
        IStripeWebhookService stripeWebhookService,
        ILogger<PaymentsController> logger)
    {
        _paymentAppService = paymentAppService;
        _stripeWebhookService = stripeWebhookService;
        _logger = logger;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateCheckoutSessionRequest request)
    {
        _logger.LogInformation("[PAYMENTS-DIAGNOSTIC] Received checkout request: Amount={Amount}, Currency='{Currency}', PaymentType='{PaymentType}', WorkspaceId={WorkspaceId}", 
            request.Amount, request.Currency, request.PaymentType, request.WorkspaceId);
        
        var url = await _paymentAppService.CreateCheckoutSessionAsync(request);
        return Ok(new { url });
    }

    [HttpGet("checkout-session/{sessionId}")]
    public async Task<IActionResult> GetCheckoutSession(string sessionId)
    {
        try
        {
            var session = await _paymentAppService.GetCheckoutSessionAsync(sessionId);

            if (session.PaymentStatus == "paid")
            {
                _logger.LogInformation("[PAYMENTS-LOCAL-FALLBACK] Processing checkout session {SessionId} directly via success page API call", session.Id);
                var isZeroDecimal = string.Equals(session.Currency, "vnd", StringComparison.OrdinalIgnoreCase);
                var finalAmount = isZeroDecimal ? (session.AmountTotal ?? 0) : ((session.AmountTotal ?? 0) / 100m);

                await _paymentAppService.ProcessPaymentEventAsync(
                    session.Id,
                    !string.IsNullOrEmpty(session.PaymentIntentId) ? session.PaymentIntentId : string.Empty,
                    finalAmount,
                    session.Currency,
                    session.Metadata.GetValueOrDefault("UserId", string.Empty),
                    session.Metadata.GetValueOrDefault("WorkspaceId", string.Empty),
                    session.Metadata.GetValueOrDefault("PaymentType", string.Empty),
                    "paid",
                    "",
                    "",
                    session.Metadata.GetValueOrDefault("PlanSlug", string.Empty),
                    session.Metadata.GetValueOrDefault("BillingCycle", string.Empty)
                );
            }

            return Ok(new
            {
                id = session.Id,
                amountTotal = session.AmountTotal,
                currency = session.Currency,
                metadata = session.Metadata,
                paymentStatus = session.PaymentStatus,
                status = session.Status,
                paymentIntentId = session.PaymentIntentId
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
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
        var stripeSignature = Request.Headers["Stripe-Signature"].ToString();

        try
        {
            var result = await _stripeWebhookService.HandleWebhookAsync(json, stripeSignature);
            if (!result)
            {
                return BadRequest("Failed to process webhook event.");
            }

            return Ok();
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogError(ex, "Stripe exception during webhook handling");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during webhook handling");
            return StatusCode(500, ex.Message);
        }
    }
}
