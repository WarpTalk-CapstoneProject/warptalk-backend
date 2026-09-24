using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// The USD→VND rate every VND report converts with (2026-09-24): Stripe's by default, recorded per UTC
/// day, overridable by a platform admin. System-admin only. Under ~/api/v1/admin/billing, which the
/// gateway's admin-billing-route catch-all already forwards to billing-service.
/// </summary>
[ApiController]
[Route("api/v1/admin/billing/fx")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminFxRateController : ControllerBase
{
    private readonly IFxRateService _fx;

    public AdminFxRateController(IFxRateService fx)
    {
        _fx = fx;
    }

    /// <summary>Today's rate, its source and as-of time, staleness, and the recorded history.</summary>
    [HttpGet]
    public async Task<ActionResult<AdminFxRateStatusDto>> Get(CancellationToken ct)
        => Ok(await _fx.GetStatusAsync(ct));

    /// <summary>Asks Stripe now. 200 with <c>error</c> set when Stripe gave nothing — the status still answers.</summary>
    [HttpPost("refresh")]
    [AdminAudited(AdminAuditBillingActions.FxRateRefreshed, AdminAuditEntityTypes.FxRate, typeof(FxRate), typeof(BillingPricingConfig), Aggregate = true)]
    public async Task<ActionResult<AdminFxRefreshResultDto>> Refresh(CancellationToken ct)
        => Ok(await _fx.RefreshAsync(force: true, ct));

    /// <summary>Use this rate instead of Stripe's, from today until the override is removed. 400 on a non-positive rate.</summary>
    [HttpPut("override")]
    [AdminAudited(AdminAuditBillingActions.FxRateOverridden, AdminAuditEntityTypes.FxRate, typeof(FxRate), typeof(BillingPricingConfig))]
    public async Task<IActionResult> SetOverride([FromBody] SetFxOverrideRequest request, CancellationToken ct)
        => ToActionResult(await _fx.SetOverrideAsync(request.Rate, ct));

    /// <summary>Back to Stripe's rate from today. Days the override covered keep it.</summary>
    [HttpDelete("override")]
    [AdminAudited(AdminAuditBillingActions.FxRateOverrideCleared, AdminAuditEntityTypes.FxRate, typeof(FxRate), typeof(BillingPricingConfig))]
    public async Task<IActionResult> ClearOverride(CancellationToken ct)
        => ToActionResult(await _fx.ClearOverrideAsync(ct));

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);
        return result.ErrorCode == ErrorCodes.ValidationError
            ? BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode))
            : StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
    }
}
