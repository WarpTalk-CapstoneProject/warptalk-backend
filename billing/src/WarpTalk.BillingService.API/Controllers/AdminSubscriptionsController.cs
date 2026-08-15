using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

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

    public AdminSubscriptionsController(IAdminSubscriptionService adminSubscriptionService)
    {
        _adminSubscriptionService = adminSubscriptionService;
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

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
