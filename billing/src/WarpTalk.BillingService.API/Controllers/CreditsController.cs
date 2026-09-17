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

    /// <summary>
    /// The workspace's credit balance. WT-700.
    ///
    /// Readable by every active internal member, not just Owner/Admin: members spend these
    /// credits in meetings and need to see how much is left. External members are guests from
    /// another organisation and have no business reading this workspace's balance. History and
    /// usage-by-member stay behind the Owner/Admin gate — the balance is one number, those show
    /// what was spent and by whom (WT-413).
    /// </summary>
    [HttpGet("workspace/{workspaceId}")]
    [RequireInternalWorkspaceMember]
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
    /// Same role gate as the history endpoint beside it — an ordinary member must not be able to
    /// read the whole workspace's spend. The balance endpoint is deliberately looser (WT-700);
    /// do not align this one with it.
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
