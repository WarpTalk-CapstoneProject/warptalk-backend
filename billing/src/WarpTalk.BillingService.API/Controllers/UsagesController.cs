using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.API.Extensions;
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
    private readonly IBillingAnalyticsService _analyticsService;
    private readonly IUsageRateCardAdminService _rateCardAdminService;

    public UsagesController(
        IUsageService usageService,
        IBillingAnalyticsService analyticsService,
        IUsageRateCardAdminService rateCardAdminService)
    {
        _usageService = usageService;
        _analyticsService = analyticsService;
        _rateCardAdminService = rateCardAdminService;
    }

    [HttpPost("record-usage")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<CreditBalanceDto>> RecordUsage([FromBody] RecordUsageRequest request, CancellationToken cancellationToken)
    {
        var result = await _usageService.RecordUsageAsync(request, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("workspace/{workspaceId}/report")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<BillingReportDto>> GetBillingReport(Guid workspaceId, [FromQuery] BillingReportQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetBillingReportAsync(workspaceId, query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("workspace/{workspaceId}/chart")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<UsageChartDto>> GetWorkspaceUsageChart(Guid workspaceId, [FromQuery] UsageChartQuery query, CancellationToken cancellationToken)
    {
        var result = await _analyticsService.GetWorkspaceUsageChartAsync(workspaceId, query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("workspace/{workspaceId}/breakdown")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoption(
        Guid workspaceId,
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetWorkspaceFeatureAdoptionAsync(workspaceId, query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("metrics/global")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<GlobalBillingMetricsDto>> GetGlobalMetrics(CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetGlobalMetricsAsync(cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("metrics/global/chart")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<UsageChartDto>> GetGlobalUsageChart([FromQuery] UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetGlobalUsageChartAsync(query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("metrics/global/breakdown")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdown(
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetGlobalUsageBreakdownAsync(query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("metrics/global/top-workspaces")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<IEnumerable<TopWorkspaceDto>>> GetTopWorkspaces(
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetTopWorkspacesAsync(query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("metrics/global/alerts")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<IEnumerable<UsageAlertDto>>> GetUsageAlerts(CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetUsageAlertsAsync(cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("rate-card")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<IReadOnlyList<UsageRateCardDto>>> GetUsageRateCard(CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.GetActiveRateCardsAsync(cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPut("rate-card")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<UsageRateCardDto>> UpsertUsageRateCard([FromBody] UpsertUsageRateCardRequest request, CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.UpsertRateCardAsync(request, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("pricing-config")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<PricingConfigDto>> GetPricingConfig(CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.GetPricingConfigAsync(cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPut("pricing-config")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<PricingConfigDto>> UpdatePricingConfig([FromBody] UpdatePricingConfigRequest request, CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.UpdatePricingConfigAsync(request, cancellationToken);
        return result.ToActionResult(this);
    }
}
