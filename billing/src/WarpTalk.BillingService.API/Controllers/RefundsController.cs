using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;


namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class RefundsController : ControllerBase
{
    private readonly IRefundService _refundService;

    public RefundsController(IRefundService refundService)
    {
        _refundService = refundService;
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<RefundDto>> RefundPayment([FromBody] RefundPaymentRequest request, CancellationToken cancellationToken)
    {
        var result = await _refundService.RefundPaymentAsync(
            request.PaymentId,
            request,
            cancellationToken);

        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        errorCode switch
        {
            "NOT_FOUND" => NotFound(new ApiErrorResponse(error ?? "Not found", errorCode)),
            "INVALID_REQUEST" => BadRequest(new ApiErrorResponse(error ?? "Invalid request", errorCode)),
            _ => StatusCode(500, new ApiErrorResponse(error ?? "An unexpected error occurred", errorCode ?? ErrorCodes.InternalServerError))
        };
}
