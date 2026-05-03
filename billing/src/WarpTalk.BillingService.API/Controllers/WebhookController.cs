using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/webhook")]
public class WebhookController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(
        IPaymentService paymentService,
        ILogger<WebhookController> logger)
    {
        _paymentService = paymentService;
        _logger = logger;
    }

    // ===================================================
    // PAYOS WEBHOOK ENTRY
    // ===================================================
    [HttpPost("payos")]
    public async Task<IActionResult> PayOsWebhook(
    [FromBody] PayOsWebhookPayload payload,
    CancellationToken ct)
    {
        try
        {
            if (payload?.Data == null)
            {
                return BadRequest(new { message = "Invalid payload" });
            }

            var orderCode = payload.Data.OrderCode;

            _logger.LogInformation(
                "PayOS webhook received | OrderCode: {OrderCode} | Code: {Code}",
                orderCode,
                payload.Code);

            var result = await _paymentService.ProcessPayOsWebhookAsync(payload, ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayOS webhook processing failed");
            return StatusCode(500, new
            {
                message = "Webhook processing failed"
            });
        }
    }
}