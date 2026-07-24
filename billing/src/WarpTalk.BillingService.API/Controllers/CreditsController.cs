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
    [Authorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<CreditBalanceDto>> GetWorkspaceCredits(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [HttpPost("consume-direct")]
    public async Task<ActionResult<CreditTransactionDto>> ConsumeCreditsDirectly([FromBody] ConsumeCreditsRequest request, CancellationToken cancellationToken)
    {
        var result = await _creditService.ConsumeCreditsDirectlyAsync(request.WorkspaceId, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [HttpPost("topup")]
    public async Task<ActionResult<CreditBalanceDto>> TopUpCredits([FromBody] TopUpRequest request, CancellationToken cancellationToken)
    {
        var result = await _creditService.TopUpCreditsAsync(request.WorkspaceId, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/history")]
    [Authorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetCreditHistory(Guid workspaceId, [FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetCreditHistoryAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [HttpPost("simulate-payment")]
    public async Task<ActionResult> SimulatePayment([FromBody] SimulatePaymentRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.SimulatePaymentAsync(request.WorkspaceId, request.Amount, request.Currency, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        
        return Ok(result.Value);
    }

    [HttpPost("manual-adjust")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CreditTransactionDto>> ManualAdjustCredits([FromBody] ManualAdjustCreditsRequest request, CancellationToken cancellationToken)
    {
        var adminUserId = User.GetUserId()?.ToString() ?? Guid.Empty.ToString();

        var adjustRequest = request with { AdminUserId = adminUserId };
        var result = await _creditService.ManualAdjustCreditsAsync(adjustRequest.WorkspaceId, adjustRequest, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [HttpGet("history/global")]
    [AllowAnonymous]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistory([FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetGlobalCreditHistoryAsync(query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }
}
