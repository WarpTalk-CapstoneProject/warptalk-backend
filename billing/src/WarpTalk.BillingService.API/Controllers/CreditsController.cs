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
using WarpTalk.Shared.Authorization;

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

    // Manual credit adjustment moved to POST ~/api/v1/admin/billing/workspaces/{id}/credits/adjust
    // (AdminWorkspaceBillingController): system-admin POLICY rather than Roles = "Admin, admin"
    // (which admits the global Admin row), a bounded amount, and an entry in the platform audit log
    // recorded before the adjustment is saved. This route did none of the three, so it is gone
    // rather than kept as a second, unaudited door to the same balance.

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
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistory([FromQuery] GlobalCreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetGlobalCreditHistoryAsync(query, cancellationToken);
        return this.ToActionResult(result);
    }

}
