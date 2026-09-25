using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Events;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Authorization;



namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class UsagesController : ControllerBase
{
    private readonly IBillingAnalyticsService _analyticsService;
    private readonly IUsageRateCardAdminService _rateCardAdminService;

    public UsagesController(
        IBillingAnalyticsService analyticsService,
        IUsageRateCardAdminService rateCardAdminService)
    {
        _analyticsService = analyticsService;
        _rateCardAdminService = rateCardAdminService;
    }


    [HttpGet("workspace/{workspaceId}/report")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<BillingReportDto>> GetBillingReport(Guid workspaceId, [FromQuery] BillingReportQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetBillingReportAsync(workspaceId, query, cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("workspace/{workspaceId}/chart")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<UsageChartDto>> GetWorkspaceUsageChart(Guid workspaceId, [FromQuery] UsageChartQuery query, CancellationToken cancellationToken)
    {
        var result = await _analyticsService.GetWorkspaceUsageChartAsync(workspaceId, query, cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("workspace/{workspaceId}/breakdown")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoption(
        Guid workspaceId,
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetWorkspaceFeatureAdoptionAsync(workspaceId, query, cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("metrics/global")]
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<GlobalBillingMetricsDto>> GetGlobalMetrics(CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetGlobalMetricsAsync(cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("metrics/global/chart")]
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<UsageChartDto>> GetGlobalUsageChart([FromQuery] UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetGlobalUsageChartAsync(query, cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("metrics/global/breakdown")]
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdown(
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetGlobalUsageBreakdownAsync(query, cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("metrics/global/top-workspaces")]
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<IEnumerable<TopWorkspaceDto>>> GetTopWorkspaces(
        [FromQuery] UsageChartQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetTopWorkspacesAsync(query, cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("metrics/global/alerts")]
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<IEnumerable<UsageAlertDto>>> GetUsageAlerts(CancellationToken cancellationToken = default)
    {
        var result = await _analyticsService.GetUsageAlertsAsync(cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpGet("rate-card")]
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<IReadOnlyList<UsageRateCardDto>>> GetUsageRateCard(CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.GetActiveRateCardsAsync(cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpPut("rate-card")]
    [AdminAudited(AdminAuditBillingActions.RateCardUpserted, AdminAuditEntityTypes.UsageRate, typeof(UsageRateCard))]
    [RequirePermission(AdminPermissions.BillingPricingManage)]
    public async Task<ActionResult<UsageRateCardDto>> UpsertUsageRateCard([FromBody] UpsertUsageRateCardRequest request, CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.UpsertRateCardAsync(request, cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpPost("rate-card/{id:guid}/deactivate")]
    [AdminAudited(AdminAuditBillingActions.RateCardDeactivated, AdminAuditEntityTypes.UsageRate, typeof(UsageRateCard), EntityRouteKey = "id")]
    [RequirePermission(AdminPermissions.BillingPricingManage)]
    public async Task<ActionResult<UsageRateCardDto>> DeactivateUsageRateCard(Guid id, CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.DeactivateRateCardAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            var error = new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode);
            return result.ErrorCode == ErrorCodes.NotFound ? NotFound(error) : BadRequest(error);
        }
        return Ok(result.Value);
    }

    [HttpPut("rate-card/{id:guid}/provider-cost")]
    [AdminAudited(AdminAuditBillingActions.RateCardCostSet, AdminAuditEntityTypes.UsageRate, typeof(UsageRateCard), EntityRouteKey = "id")]
    [RequirePermission(AdminPermissions.BillingPricingManage)]
    public async Task<ActionResult<UsageRateCardDto>> SetUsageRateCardProviderCost(
        Guid id, [FromBody] SetRateCardProviderCostRequest request, CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.SetProviderCostAsync(id, request, cancellationToken);
        if (!result.IsSuccess)
        {
            var error = new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode);
            return result.ErrorCode switch
            {
                ErrorCodes.NotFound => NotFound(error),
                ErrorCodes.InternalServerError => StatusCode(500, error),
                _ => BadRequest(error),
            };
        }
        return Ok(result.Value);
    }

    [HttpPost("rate-card/preview")]
    [RequirePermission(AdminPermissions.BillingPricingManage)]
    public async Task<ActionResult<RateCardPreviewDto>> PreviewUsageRateCard([FromBody] RateCardPreviewRequest request, CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.PreviewRateCardAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpGet("pricing-config")]
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<PricingConfigDto>> GetPricingConfig(CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.GetPricingConfigAsync(cancellationToken);
        return this.ToActionResult(result);
    }

    [HttpPut("pricing-config")]
    [AdminAudited(AdminAuditBillingActions.PricingConfigUpdated, AdminAuditEntityTypes.PricingConfig, typeof(BillingPricingConfig))]
    [RequirePermission(AdminPermissions.BillingPricingManage)]
    public async Task<ActionResult<PricingConfigDto>> UpdatePricingConfig([FromBody] UpdatePricingConfigRequest request, CancellationToken cancellationToken)
    {
        var result = await _rateCardAdminService.UpdatePricingConfigAsync(request, cancellationToken);
        return this.ToActionResult(result);
    }
}
