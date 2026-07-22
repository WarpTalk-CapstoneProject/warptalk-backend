using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;


namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class UsagesController : ControllerBase
{
    private readonly IUsageService _usageService;

    public UsagesController(IUsageService usageService)
    {
        _usageService = usageService;
    }

    [HttpPost("record-usage")]
    public async Task<ActionResult> RecordUsage([FromBody] RecordUsageRequest request, CancellationToken cancellationToken)
    {
        var result = await _usageService.RecordUsageAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/report")]
    [Authorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<BillingReportDto>> GetBillingReport(Guid workspaceId, [FromQuery] BillingReportQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetBillingReportAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/chart")]
    [Authorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<UsageChartDto>> GetWorkspaceUsageChart(Guid workspaceId, [FromQuery] UsageChartQuery query, CancellationToken cancellationToken)
    {
        var result = await _usageService.GetWorkspaceUsageChartAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/breakdown")]
    [AllowAnonymous]
    public async Task<ActionResult<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoption(
        Guid workspaceId,
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetWorkspaceFeatureAdoptionAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global")]
    [AllowAnonymous]
    public async Task<ActionResult<GlobalBillingMetricsDto>> GetGlobalMetrics(CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetGlobalMetricsAsync(cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/chart")]
    [AllowAnonymous]
    public async Task<ActionResult<UsageChartDto>> GetGlobalUsageChart([FromQuery] UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetGlobalUsageChartAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/breakdown")]
    [AllowAnonymous]
    public async Task<ActionResult<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdown(
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetGlobalUsageBreakdownAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/top-workspaces")]
    [AllowAnonymous]
    public async Task<ActionResult<IEnumerable<TopWorkspaceDto>>> GetTopWorkspaces(
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetTopWorkspacesAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/alerts")]
    [AllowAnonymous]
    public async Task<ActionResult<IEnumerable<UsageAlertDto>>> GetUsageAlerts(CancellationToken cancellationToken = default)
    {
        var result = await _usageService.GetUsageAlertsAsync(cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("rates")]
    [Authorize]
    public ActionResult<ServiceRatesDto> GetServiceRates()
    {
        var result = _usageService.GetServiceRates();
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpPut("rates")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ServiceRatesDto>> UpdateServiceRates([FromBody] UpdateServiceRatesRequest request, CancellationToken cancellationToken)
    {

        var result = await _usageService.UpdateServiceRatesAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        errorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new { message = error }),
            ErrorCodes.BillingInsufficientCredits => UnprocessableEntity(new { message = error }),
            "FEATURE_NOT_AVAILABLE" => StatusCode(403, new { message = error }),
            "INVALID_REQUEST" => BadRequest(new { message = error }),
            _ => StatusCode(500, new { message = error })
        };
}
