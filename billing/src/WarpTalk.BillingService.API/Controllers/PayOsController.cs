using System.Threading;
using System.Threading.Tasks;
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
        catch (System.Exception ex)
        {
            // Even if signature fails, we might return 400 as per spec
            return BadRequest(new { message = ex.Message });
        }
    }
}
