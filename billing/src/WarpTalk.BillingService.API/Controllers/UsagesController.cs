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
[Route("api/v1/[controller]")]
public class UsagesController : ControllerBase
{
    private readonly IUsageService _usageService;
    private readonly IBillingAnalyticsService _analyticsService;
    private readonly IBillingRateService _rateService;

    public UsagesController(
        IUsageService usageService,
        IBillingAnalyticsService analyticsService,
        IBillingRateService rateService)
    {
        _usageService = usageService;
        _analyticsService = analyticsService;
        _rateService = rateService;
    }

    [HttpPost("record-usage")]
    public async Task<ActionResult> RecordUsage([FromBody] RecordUsageRequest request, CancellationToken cancellationToken)
    {
        var result = await _usageService.RecordUsageAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/report")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<BillingReportDto>> GetBillingReport(Guid workspaceId, [FromQuery] BillingReportQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetBillingReportAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/chart")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<UsageChartDto>> GetWorkspaceUsageChart(Guid workspaceId, [FromQuery] UsageChartQuery query, CancellationToken cancellationToken)
    {
        var result = await _analyticsService.GetWorkspaceUsageChartAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}/breakdown")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoption(
        Guid workspaceId,
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetWorkspaceFeatureAdoptionAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<GlobalBillingMetricsDto>> GetGlobalMetrics(CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetGlobalMetricsAsync(cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/chart")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UsageChartDto>> GetGlobalUsageChart([FromQuery] UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetGlobalUsageChartAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/breakdown")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdown(
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetGlobalUsageBreakdownAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/top-workspaces")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<TopWorkspaceDto>>> GetTopWorkspaces(
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetTopWorkspacesAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("metrics/global/alerts")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<UsageAlertDto>>> GetUsageAlerts(CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetUsageAlertsAsync(cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpGet("rates")]
    [Authorize]
    public ActionResult<ServiceRatesDto> GetServiceRates()
    {
        var result = _rateService.GetServiceRates();
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    [HttpPut("rates")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ServiceRatesDto>> UpdateServiceRates([FromBody] UpdateServiceRatesRequest request, CancellationToken cancellationToken)
    {
        var result = await _rateService.UpdateServiceRatesAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        return Ok(result.Value);
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        errorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new ApiErrorResponse(error ?? "Subscription not found", errorCode)),
            ErrorCodes.BillingInsufficientCredits => UnprocessableEntity(new ApiErrorResponse(error ?? "Insufficient credits", errorCode)),
            "FEATURE_NOT_AVAILABLE" => StatusCode(403, new ApiErrorResponse(error ?? "Feature not available", errorCode)),
            "INVALID_REQUEST" => BadRequest(new ApiErrorResponse(error ?? "Invalid request", errorCode)),
            _ => StatusCode(500, new ApiErrorResponse(error ?? "An unexpected error occurred", errorCode ?? ErrorCodes.InternalServerError))
        };
}
