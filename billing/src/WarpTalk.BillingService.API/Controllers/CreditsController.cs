using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class CreditsController : ControllerBase
{
    private readonly ICreditService _creditService;
    private readonly IWebHostEnvironment _environment;

    public CreditsController(
        ICreditService creditService,
        IWebHostEnvironment environment)
    {
        _creditService = creditService;
        _environment = environment;
    }

    [HttpGet("workspace/{workspaceId}")]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdmin)]
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
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner)]
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
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner)]
    public async Task<ActionResult<CreditBalanceDto>> TopUpCredits([FromBody] TopUpRequest request, CancellationToken cancellationToken)
    {
        var result = await _creditService.TopUpCreditsAsync(request.WorkspaceId, request, cancellationToken);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.Forbidden)
            {
                return StatusCode(StatusCodes.Status410Gone, new ApiErrorResponse(result.Error, result.ErrorCode));
            }

            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/history")]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdmin)]
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
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var result = await _creditService.SimulatePaymentAsync(request.WorkspaceId, request.Amount, request.Currency, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        
        return Ok(result.Value);
    }

    [HttpPost("manual-adjust")]
    [Authorize(Roles = WorkspaceRoleConstants.Admin)]
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
    [Authorize(Roles = WorkspaceRoleConstants.Admin)]
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
