using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Extensions;
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
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<CreditBalanceDto>> GetWorkspaceCredits(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPost("consume-direct")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner)]
    public async Task<ActionResult<CreditTransactionDto>> ConsumeCreditsDirectly([FromBody] ConsumeCreditsRequest request, CancellationToken cancellationToken)
    {
        var result = await _creditService.ConsumeCreditsDirectlyAsync(request.WorkspaceId, request, cancellationToken);
        return result.ToActionResult(this);
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
                return this.ToErrorResult(StatusCodes.Status410Gone, result.Error, result.ErrorCode);
            }

            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/history")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetCreditHistory(Guid workspaceId, [FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetCreditHistoryAsync(workspaceId, query, cancellationToken);
        return result.ToActionResult(this);
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
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return Ok(result.Value);
    }

    [HttpPost("manual-adjust")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<CreditTransactionDto>> ManualAdjustCredits([FromBody] ManualAdjustCreditsRequest request, CancellationToken cancellationToken)
    {
        var adminUserId = User.GetUserId()?.ToString() ?? Guid.Empty.ToString();

        var adjustRequest = request with { AdminUserId = adminUserId };
        var result = await _creditService.ManualAdjustCreditsAsync(adjustRequest.WorkspaceId, adjustRequest, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("history/global")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistory([FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetGlobalCreditHistoryAsync(query, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }
        return Ok(result.Value);
    }
}

