using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;


namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentAndLedgerService _paymentService;

    public PaymentsController(IPaymentAndLedgerService paymentService)
    {
        _paymentService = paymentService;
    }

    [HttpGet("workspace/{workspaceId}/history")]
    [Authorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<PaginatedResponse<PaymentTransactionDto>>> GetPaymentHistory(
        Guid workspaceId,
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _paymentService.GetPaymentHistoryAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }




    [HttpPost]
    [Authorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<PaymentTransactionDto>> CreatePayment([FromBody] CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var result = await _paymentService.CreatePaymentAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return StatusCode(201, result.Value);
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook([FromBody] PaymentWebhookRequest request, CancellationToken cancellationToken)
    {
        var result = await _paymentService.HandleWebhookAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(new { message = "Webhook processed successfully." });
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        errorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new { message = error }),
            ErrorCodes.BillingPlanNotFound => BadRequest(new { message = error }),
            "FEATURE_NOT_AVAILABLE" => StatusCode(403, new { message = error }),
            _ => StatusCode(500, new { message = error })
        };
}

