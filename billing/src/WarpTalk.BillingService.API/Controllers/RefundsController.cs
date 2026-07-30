using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.BillingService.API.Filters;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/refunds")]
public class RefundsController : ControllerBase
{
    private readonly IRefundService _refundService;

    public RefundsController(IRefundService refundService)
    {
        _refundService = refundService;
    }

    /// <summary>
    /// Process a refund for a specific payment transaction (Admin only).
    /// </summary>
    [HttpPost("payment/{paymentId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<RefundDto>> RefundPayment(
        Guid paymentId,
        [FromBody] RefundPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _refundService.RefundPaymentAsync(
            paymentId,
            request.Amount,
            request.Reason,
            cancellationToken);

        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            "NOT_FOUND" => NotFound(new { message = result.Error }),
            "INVALID_REQUEST" => BadRequest(new { message = result.Error }),
            _ => StatusCode(500, new { message = result.Error })
        };
}
