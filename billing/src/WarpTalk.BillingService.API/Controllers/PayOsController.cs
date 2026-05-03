using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Security;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/payos")]
public class PayOsController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PayOsController> _logger;

    public PayOsController(
        IPaymentService paymentService,
        IConfiguration configuration,
        ILogger<PayOsController> logger)
    {
        _paymentService = paymentService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Nhận Webhook từ PayOS - Validates signature using HMAC-SHA256
    /// </summary>
    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> HandlePayOsWebhook(
        [FromBody] PayOsWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Invalid webhook payload received. TraceId={TraceId}", HttpContext.TraceIdentifier);
            return BadRequest(new { message = "Invalid payload structure" });
        }

        try
        {
            // Validate webhook signature if configured
            var allowInsecureInDev = _configuration.GetValue<bool>("Security:AllowInsecureWebhookSignatureInDevelopment");
            if (!allowInsecureInDev)
            {
                // Extract signature from header
                if (!HttpContext.Request.Headers.TryGetValue("X-Signature", out var signatureHeader))
                {
                    _logger.LogWarning("Missing X-Signature header. TraceId={TraceId}", HttpContext.TraceIdentifier);
                    return Unauthorized(new { message = "Missing signature" });
                }

                // Get checksum key from config
                var checksumKey = _configuration["PayOS:ChecksumKey"];
                if (string.IsNullOrEmpty(checksumKey))
                {
                    _logger.LogError("PayOS ChecksumKey not configured. TraceId={TraceId}", HttpContext.TraceIdentifier);
                    return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Configuration error" });
                }

                // Validate signature
                var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
                if (!WebhookSignatureValidator.ValidatePayOsSignature(payloadJson, signatureHeader.ToString(), checksumKey))
                {
                    _logger.LogWarning("Invalid webhook signature. TraceId={TraceId}", HttpContext.TraceIdentifier);
                    return Unauthorized(new { message = "Invalid signature" });
                }
            }

            var result = await _paymentService.ProcessPayOsWebhookAsync(payload, cancellationToken);

            return result.ResultCode switch
            {
                "ORDER_NOT_FOUND" => NotFound(result),
                "INVALID_PAYLOAD" => BadRequest(result),
                "INVALID_AMOUNT" => BadRequest(result),
                _ => Ok(result)
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized access in webhook. TraceId={TraceId}", HttpContext.TraceIdentifier);
            return Unauthorized(new { message = "Invalid PayOS credentials" });
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Webhook processing failed. TraceId={TraceId}", HttpContext.TraceIdentifier);
            return BadRequest(new
            {
                message = "Webhook processing failed",
                traceId = HttpContext.TraceIdentifier
            });
        }
    }
}
