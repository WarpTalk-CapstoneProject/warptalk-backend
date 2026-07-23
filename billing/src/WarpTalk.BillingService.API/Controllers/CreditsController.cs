using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.BillingService.API.Filters;
using System.Security.Claims;



namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class CreditsController : ControllerBase
{
    private readonly ICreditService _creditService;
    public CreditsController(ICreditService creditService)
    {
        _creditService = creditService;
    }

    [HttpGet("workspace/{workspaceId}")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<CreditBalanceDto>> GetWorkspaceCredits(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPost("consume-direct")]
    public async Task<ActionResult<CreditTransactionDto>> ConsumeCreditsDirectly([FromBody] ConsumeCreditsRequest request, CancellationToken cancellationToken)
    {
        var result = await _creditService.ConsumeCreditsDirectlyAsync(request.WorkspaceId, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPost("topup")]
    public async Task<ActionResult<CreditBalanceDto>> TopUpCredits([FromBody] TopUpRequest request, CancellationToken cancellationToken)
    {
        var result = await _creditService.TopUpCreditsAsync(request.WorkspaceId, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/history")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetCreditHistory(Guid workspaceId, [FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetCreditHistoryAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPost("simulate-payment")]
    public async Task<ActionResult> SimulatePayment([FromBody] SimulatePaymentRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.SimulatePaymentAsync(request.WorkspaceId, request.Amount, request.Currency, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        
        return Ok(result.Value);
    }

    [HttpPost("manual-adjust")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CreditTransactionDto>> ManualAdjustCredits([FromBody] ManualAdjustCreditsRequest request, CancellationToken cancellationToken)
    {
        var adminUserId = User.GetUserId()?.ToString() ?? Guid.Empty.ToString();

        var adjustRequest = request with { AdminUserId = adminUserId };
        var result = await _creditService.ManualAdjustCreditsAsync(adjustRequest.WorkspaceId, adjustRequest, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("history/global")]
    [AllowAnonymous]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistory([FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetGlobalCreditHistoryAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        errorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new ApiErrorResponse(error ?? "Subscription not found", errorCode)),
            ErrorCodes.BillingInsufficientCredits => UnprocessableEntity(new ApiErrorResponse(error ?? "Insufficient credits", errorCode)),
            "FEATURE_NOT_AVAILABLE" => StatusCode(403, new ApiErrorResponse(error ?? "Feature not available", errorCode)),
            "INVALID_REQUEST" => BadRequest(new ApiErrorResponse(error ?? "Invalid request", errorCode)),
            _ => StatusCode(500, new ApiErrorResponse(error ?? "An unexpected error occurred", errorCode ?? ErrorCodes.InternalServerError))
        };
}
