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
    [Authorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<CreditBalanceDto>> GetWorkspaceCredits(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPost("consume")]
    public async Task<ActionResult<CreditTransactionDto>> ConsumeCredits([FromBody] ConsumeCreditsRequest request, CancellationToken cancellationToken)
    {
        var result = await _creditService.ConsumeCreditsAsync(request.WorkspaceId, request, cancellationToken);
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
    [Authorize(Roles = "Owner, Admin")]
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

    [HttpPost("adjust")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CreditTransactionDto>> AdjustCredits([FromBody] AdjustCreditsRequest request, CancellationToken cancellationToken)
    {
        var adminUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? Guid.Empty.ToString();

        var adjustRequest = request with { AdminUserId = adminUserId };
        var result = await _creditService.AdjustCreditsAsync(adjustRequest.WorkspaceId, adjustRequest, cancellationToken);
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
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new { message = error }),
            ErrorCodes.BillingInsufficientCredits => UnprocessableEntity(new { message = error }),
            "FEATURE_NOT_AVAILABLE" => StatusCode(403, new { message = error }),
            "INVALID_REQUEST" => BadRequest(new { message = error }),
            _ => StatusCode(500, new { message = error })
        };
}
