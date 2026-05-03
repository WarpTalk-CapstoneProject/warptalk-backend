using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Security;
using WarpTalk.BillingService.Application.Services.Interface;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/quota")]
public class QuotaController : ControllerBase
{
    private readonly IQuotaService _quotaService;
    private readonly IWorkspaceOwnershipResolver _workspaceOwnershipResolver;

    public QuotaController(
        IQuotaService quotaService,
        IWorkspaceOwnershipResolver workspaceOwnershipResolver)
    {
        _quotaService = quotaService;
        _workspaceOwnershipResolver = workspaceOwnershipResolver;
    }

    // ===================================================
    // TOP UP QUOTA (thay cho Deduct + Refund logic)
    // ===================================================
    [HttpPost("topup")]
    public async Task<IActionResult> TopUp(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromBody] decimal credits,
        CancellationToken ct)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null) return accessError;

        var ownerId = await _workspaceOwnershipResolver.ResolveOwnerUserIdAsync(workspaceId, ct);

        var result = await _quotaService.TopUpQuotaByOwnerAsync(ownerId, credits, null, ct);

        return Ok(result);
    }

    // ===================================================
    // UPGRADE PLAN
    // ===================================================
    [HttpPost("upgrade")]
    public async Task<IActionResult> UpgradePlan(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromBody] Guid planId,
        CancellationToken ct)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null) return accessError;

        var ownerId = await _workspaceOwnershipResolver.ResolveOwnerUserIdAsync(workspaceId, ct);

        var success = await _quotaService.UpgradePlanByOwnerAsync(ownerId, planId, ct);

        return success
            ? Ok(new { message = "Upgraded successfully" })
            : BadRequest(new { message = "Upgrade failed" });
    }
}