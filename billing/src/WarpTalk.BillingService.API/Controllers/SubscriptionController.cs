using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Security;
using WarpTalk.BillingService.Application.Services.Interface;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/subscription")]
public class SubscriptionController : ControllerBase
{
    private readonly IQuotaService _quotaService;
    private readonly IWorkspaceOwnershipResolver _workspaceOwnershipResolver;

    public SubscriptionController(IQuotaService quotaService, IWorkspaceOwnershipResolver workspaceOwnershipResolver)
    {
        _quotaService = quotaService;
        _workspaceOwnershipResolver = workspaceOwnershipResolver;
    }

    [HttpPost("upgrade")]
    public async Task<IActionResult> UpgradeSubscription(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromBody] Guid planId,
        CancellationToken cancellationToken)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null)
        {
            return accessError;
        }

        var ownerId = await _workspaceOwnershipResolver.ResolveOwnerUserIdAsync(workspaceId, cancellationToken);
        var success = await _quotaService.UpgradePlanByOwnerAsync(ownerId, planId, cancellationToken);
        if (success)
        {
            return Ok(new { message = "Subscription upgraded successfully" });
        }

        return BadRequest(new { message = "Failed to upgrade subscription." });
    }
}