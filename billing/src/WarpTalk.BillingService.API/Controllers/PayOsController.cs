using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;

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
            var result = await _paymentService.ProcessPayOsWebhookAsync(payload, cancellationToken);

            return result.ResultCode switch
            {
                "ORDER_NOT_FOUND" => NotFound(result),
                "INVALID_PAYLOAD" => BadRequest(result),
                "INVALID_AMOUNT" => BadRequest(result),
                _ => Ok(result)
            };
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
