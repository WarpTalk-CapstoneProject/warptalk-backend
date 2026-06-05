using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/payments")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;

    public PaymentsController(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <summary>
    /// Paginated payment/transaction history for a workspace.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}/history")]
    public async Task<ActionResult<PagedResult<PaymentTransactionDto>>> GetPaymentHistory(
        Guid workspaceId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _paymentService.GetPaymentHistoryAsync(workspaceId, pageNumber, pageSize, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    // GetInvoicesAsync removed per cleanup

    /// <summary>
    /// Create a pending payment checkout for a subscription.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PaymentTransactionDto>> CreatePayment(
        [FromBody] CreatePaymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _paymentService.CreatePaymentAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return StatusCode(201, result.Value);
    }

    /// <summary>
    /// Simulate or receive a payment provider webhook to activate a subscription.
    /// </summary>
    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook(
        [FromBody] PaymentWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _paymentService.HandleWebhookAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(new { message = "Webhook processed successfully." });
    }

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new { message = result.Error }),
            ErrorCodes.BillingPlanNotFound => BadRequest(new { message = result.Error }),
            "FEATURE_NOT_AVAILABLE" => StatusCode(403, new { message = result.Error }),
            _ => StatusCode(500, new { message = result.Error })
        };
}

