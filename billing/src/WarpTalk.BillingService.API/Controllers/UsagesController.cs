using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.BillingService.API.Filters;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/usages")]
public class UsagesController : ControllerBase
{
    private readonly IUsageService _usageService;

    public UsagesController(IUsageService usageService)
    {
        _usageService = usageService;
    }

    /// <summary>
    /// Record usage for a workspace.
    /// </summary>
    [HttpPost("workspace/{workspaceId:guid}/record-usage")]
    [Authorize(Roles = "Admin,billing_admin")]
    public async Task<ActionResult> RecordUsage(
        Guid workspaceId,
        [FromBody] RecordUsageRequest request,
        CancellationToken cancellationToken)
    {
        var actualRequest = new RecordUsageRequest(
            workspaceId,
            request.UserId,
            request.UsageType,
            request.Unit,
            request.Quantity,
            request.CreditsConsumed,
            request.DurationSeconds,
            request.TranslationRoomId,
            request.SegmentId,
            request.Details);

        var result = await _usageService.RecordUsageAsync(actualRequest, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Generate a billing report for a workspace for a specific month and year.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}/report")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<BillingReportDto>> GetBillingReport(
        Guid workspaceId,
        [FromQuery] int month,
        [FromQuery] int year,
        CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetBillingReportAsync(workspaceId, year, month, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Gets usage chart data for a workspace.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}/chart")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<UsageChartDto>> GetWorkspaceUsageChart(
        Guid workspaceId,
        [FromQuery] int year,
        CancellationToken cancellationToken)
    {
        var result = await _usageService.GetWorkspaceUsageChartAsync(workspaceId, year, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return Ok(result.Value);
    }

    /// <summary>
    /// Gets feature adoption metrics for a workspace.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}/breakdown")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoption(
        Guid workspaceId,
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetWorkspaceFeatureAdoptionAsync(workspaceId, days, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<GlobalBillingMetricsDto>> GetGlobalMetrics(CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetGlobalMetricsAsync(cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/chart")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UsageChartDto>> GetGlobalUsageChart(
        [FromQuery] int year,
        CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetGlobalUsageChartAsync(year, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/breakdown")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdown(
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetGlobalUsageBreakdownAsync(days, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/top-workspaces")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<TopWorkspaceDto>>> GetTopWorkspaces(
        [FromQuery] int days = 30,
        [FromQuery] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetTopWorkspacesAsync(days, limit, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/alerts")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<UsageAlertDto>>> GetUsageAlerts(CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetUsageAlertsAsync(cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return Ok(result.Value);
    }

    /// <summary>
    /// Get current AI service credit rates (admin only).
    /// </summary>
    [HttpGet("rates")]
    [Authorize(Roles = "Admin")]
    public ActionResult<ServiceRatesDto> GetServiceRates()
    {
        var result = _usageService.GetServiceRates();
        if (!result.IsSuccess) return HandleFailure(result);
        return Ok(result.Value);
    }

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new { message = result.Error }),
            ErrorCodes.BillingInsufficientCredits => UnprocessableEntity(new { message = result.Error }),
            "FEATURE_NOT_AVAILABLE" => StatusCode(403, new { message = result.Error }),
            "INVALID_REQUEST" => BadRequest(new { message = result.Error }),
            _ => StatusCode(500, new { message = result.Error })
        };
}
