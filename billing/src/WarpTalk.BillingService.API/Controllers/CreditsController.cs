using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public CreditsController(ICreditService creditService)
    {
        _creditService = creditService;
    }

    [HttpGet("workspace/{workspaceId}")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<CreditBalanceDto>> GetWorkspaceCredits(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, cancellationToken);
        return this.ToActionResult(result);
    }

    /// <summary>
    /// Manual credit adjustment, platform admin only. The service method existed, unit-tested,
    /// for weeks with no route in front of it — the portal's Adjust Credit button posted here and
    /// 404'd. The actor comes from the token, never the body, because the adjustment is written
    /// into the audit trail under their id.
    /// </summary>
    [HttpPost("workspace/{workspaceId}/adjust")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<CreditTransactionDto>> AdjustWorkspaceCredits(
        Guid workspaceId,
        [FromBody] AdjustCreditsRequest request,
        CancellationToken cancellationToken)
    {
        var adminUserId = User.GetUserId();
        if (adminUserId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _creditService.AdjustWorkspaceCreditsAsync(
            workspaceId, request, adminUserId.Value, cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("workspace/{workspaceId}/history")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetCreditHistory(Guid workspaceId, [FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetCreditHistoryAsync(workspaceId, query, cancellationToken);
        return this.ToActionResult(result);
    }

    /// <summary>
    /// Who in this workspace has spent what. WT-413.
    ///
    /// Same role gate as the balance and history endpoints beside it — an ordinary member must
    /// not be able to read the whole workspace's spend, and RequireWorkspaceRole is what the
    /// two neighbours already use, so this cannot drift from them.
    /// </summary>
    [HttpGet("workspace/{workspaceId}/usage-by-member")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<WorkspaceUsageByMemberDto>> GetUsageByMember(
        Guid workspaceId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetUsageByMemberAsync(workspaceId, from, to, cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("history/global")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistory([FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetGlobalCreditHistoryAsync(query, cancellationToken);
        return this.ToActionResult(result);
    }

}
