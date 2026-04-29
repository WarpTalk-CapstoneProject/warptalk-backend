using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/payos")]
public class PayOsController : ControllerBase
{
    private readonly IPaymentService _paymentService;

    public PayOsController(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <summary>
    /// Nhận Webhook từ PayOS
    /// </summary>
    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> HandlePayOsWebhook([FromBody] PayOsWebhookPayload payload, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            await _paymentService.ProcessPayOsWebhookAsync(payload, cancellationToken);
            
            // PayOS expects 200 OK or specific response to acknowledge receipt
            return Ok(new { success = true });
        }
        catch (UnauthorizedAccessException)
        {
            return BadRequest(new { message = "Invalid PayOS signature." });
        }
        catch (System.Exception)
        {
            return BadRequest(new
            {
                message = "Webhook processing failed.",
                traceId = HttpContext.TraceIdentifier
            });
        }
    }
}
