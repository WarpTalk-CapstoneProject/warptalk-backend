using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Platform admin Insights, billing half (2026-09-17). Read-only, system-admin only.
///
/// Under ~/api/v1/admin/billing, which the gateway already forwards to billing-service through the
/// admin-billing-route catch-all — no gateway change is needed.
/// </summary>
[ApiController]
[Route("api/v1/admin/billing/insights")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminBillingInsightsController : ControllerBase
{
    private readonly IAdminBillingInsightsService _insights;

    public AdminBillingInsightsController(IAdminBillingInsightsService insights)
    {
        _insights = insights;
    }

    /// <summary>
    /// <c>?from&amp;to&amp;compare=previous|previousMonth&amp;tz</c> (shared AdminComparisonRange). 400 on an
    /// invalid range or an unknown tz.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetInsights([FromQuery] AdminInsightsQuery query, CancellationToken ct)
        => ToActionResult(await _insights.GetInsightsAsync(query, ct));

    /// <summary>"Right now" figures; today, yesterday and this month on the calendar of <c>?tz</c> (default Asia/Ho_Chi_Minh). 400 on an unknown tz.</summary>
    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot([FromQuery(Name = "tz")] string? tz, CancellationToken ct)
        => ToActionResult(await _insights.GetSnapshotAsync(tz, ct));

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
