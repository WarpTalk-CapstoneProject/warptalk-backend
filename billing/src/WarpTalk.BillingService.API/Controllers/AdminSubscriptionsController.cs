using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Platform-wide subscription directory and revenue summary for the System Admin portal.
///
/// Gated by the shared system-admin policy, which requires the exact platform role value "admin"
/// — distinct from the workspace-scoped "Admin". Workspace Owner/Admin/Member live in
/// workspace_members and never reach the token, so they cannot open these endpoints by any route.
/// </summary>
[ApiController]
[Route("api/v1/admin/subscriptions")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminSubscriptionsController : ControllerBase
{
    private readonly IAdminSubscriptionService _adminSubscriptionService;
    private readonly ISubscriptionService _subscriptionService;

    public AdminSubscriptionsController(
        IAdminSubscriptionService adminSubscriptionService,
        ISubscriptionService subscriptionService)
    {
        _adminSubscriptionService = adminSubscriptionService;
        _subscriptionService = subscriptionService;
    }

    [HttpGet]
    public async Task<IActionResult> GetDirectory(
        [FromQuery] AdminSubscriptionDirectoryQuery query,
        CancellationToken ct)
    {
        var result = await _adminSubscriptionService.GetDirectoryAsync(query, ct);
        return ToActionResult(result);
    }

    /// <summary>
    /// Its own endpoint, not a field on the directory. Recurring revenue is computed over EVERY
    /// active subscription; returning it beside a page of twenty would invite reading it as the
    /// page's total.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var result = await _adminSubscriptionService.GetSummaryAsync(ct);
        return ToActionResult(result);
    }

    /// <summary>
    /// Move a workspace's live subscription onto another plan. Cancel and resume keep their
    /// ordinary workspace-scoped endpoints (a platform admin passes those role checks); a plan
    /// swap has no ordinary path at all — customers change plans through checkout, which is
    /// exactly the step an administrative move must not require.
    /// </summary>
    [HttpPost("workspace/{workspaceId:guid}/change-plan")]
    public async Task<IActionResult> ChangePlan(
        Guid workspaceId,
        [FromBody] AdminChangeSubscriptionPlanRequest request,
        CancellationToken ct)
    {
        var adminUserId = User.GetUserId();
        if (adminUserId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _subscriptionService.AdminChangePlanAsync(
            workspaceId, request.PlanId, adminUserId.Value, ct);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.BillingSubscriptionNotFound or ErrorCodes.BillingPlanNotFound
                => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.BillingSubscriptionConflict
                => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
