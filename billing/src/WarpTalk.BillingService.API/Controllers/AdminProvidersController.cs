using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// The admin Providers page (2026-09-25): OpenAI, Cartesia, LiveKit and Stripe — usage, cost, our
/// calls' success rate and latency, and a 90-day uptime row. Read-only, system-admin only. Billing
/// owns it because billing owns the usage ledger, the provider cost and the FX rates; LiveKit usage
/// is read from translation-room over gRPC. The gateway forwards ~/api/v1/admin/providers here.
/// </summary>
[ApiController]
[Route("api/v1/admin/providers")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminProvidersController : ControllerBase
{
    private readonly IAdminProvidersService _providers;

    public AdminProvidersController(IAdminProvidersService providers)
    {
        _providers = providers;
    }

    /// <summary>Every provider: status, today's usage and cost, last 24 h of calls, 90-day uptime, configuration. <c>?tz</c>.</summary>
    [HttpGet]
    public async Task<IActionResult> GetOverview([FromQuery(Name = "tz")] string? tz, CancellationToken ct)
        => ToActionResult(await _providers.GetOverviewAsync(tz, ct));

    /// <summary><c>?from&amp;to&amp;tz&amp;granularity=day|hour&amp;metrics=usage,costUsd,…</c> (hourly: 14 days at most; any: 120).</summary>
    [HttpGet("{key}/series")]
    public async Task<IActionResult> GetSeries(
        string key,
        [FromQuery] AdminInsightsQuery query,
        [FromQuery] string? granularity,
        [FromQuery] string? metrics,
        CancellationToken ct)
        => ToActionResult(await _providers.GetSeriesAsync(key, query, granularity, metrics, ct));

    /// <summary><c>?by=workspace|service|model|operation|errorClass&amp;from&amp;to&amp;tz</c>.</summary>
    [HttpGet("{key}/breakdown")]
    public async Task<IActionResult> GetBreakdown(string key, [FromQuery] AdminInsightsQuery query, [FromQuery] string? by, CancellationToken ct)
        => ToActionResult(await _providers.GetBreakdownAsync(key, query, by, ct));

    /// <summary><c>?days=90&amp;tz</c>: one status per local day, the uptime percentage and its basis.</summary>
    [HttpGet("{key}/uptime")]
    public async Task<IActionResult> GetUptime(string key, [FromQuery] int? days, [FromQuery(Name = "tz")] string? tz, CancellationToken ct)
        => ToActionResult(await _providers.GetUptimeAsync(key, days, tz, ct));

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
