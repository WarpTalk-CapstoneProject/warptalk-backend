using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Events;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PlansController : ControllerBase
{
    private readonly IPlanService _planService;

    public PlansController(IPlanService planService)
    {
        _planService = planService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PlanDto>>> GetPlans(CancellationToken cancellationToken)
    {
        var result = await _planService.GetActivePlansAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// The VAT the pricing pages print next to a price (3 Oct 2026): prices are stored without VAT
    /// and <see cref="WarpTalk.Shared.PlatformSettings.PlatformSettingsCatalog.VatPercent"/> is
    /// added on top at Stripe checkout. Served here, beside the plan list it qualifies and with the
    /// same anonymous access, so the homepage, the buyer's plan cards and the admin editor print the
    /// number checkout will actually charge rather than a copy of it.
    ///
    /// Placed above `{id}` so ASP.NET does not try to bind "tax" as a Guid.
    /// </summary>
    [HttpGet("tax")]
    public async Task<ActionResult<PlanTaxDto>> GetTax(CancellationToken cancellationToken)
    {
        var settings = HttpContext.RequestServices.GetService(typeof(WarpTalk.Shared.PlatformSettings.IPlatformSettings))
            as WarpTalk.Shared.PlatformSettings.IPlatformSettings;
        var percent = settings is null
            ? 10m
            : await settings.GetDecimalAsync(WarpTalk.Shared.PlatformSettings.PlatformSettingsCatalog.VatPercent, ct: cancellationToken);
        return Ok(new PlanTaxDto(percent, PricesIncludeVat: false));
    }

    /// <summary>
    /// BR-74 — the administrator's list, deactivated plans included.
    ///
    /// A separate route rather than a `?includeInactive=true` on the one above, deliberately: a
    /// parameter that means different things depending on who sends it is one missing role check
    /// away from publishing the whole catalogue, and nothing in a URL makes that visible. Two
    /// routes make the authorization the route's own property.
    ///
    /// Placed above `{id}` so ASP.NET does not try to bind "all" as a Guid.
    /// </summary>
    [HttpGet("all")]
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<IEnumerable<PlanDto>>> GetAllPlans(CancellationToken cancellationToken)
    {
        var result = await _planService.GetAllPlansAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PlanDto>> GetPlanById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _planService.GetPlanByIdAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }


    [HttpPost]
    [AdminAudited(AdminAuditBillingActions.PlanCreated, AdminAuditEntityTypes.Plan, typeof(Plan))]
    [RequirePermission(AdminPermissions.BillingPlansManage)]
    public async Task<ActionResult<PlanDto>> CreatePlan([FromBody] PlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _planService.CreatePlanAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpPut("{id}")]
    [AdminAudited(AdminAuditBillingActions.PlanUpdated, AdminAuditEntityTypes.Plan, typeof(Plan), EntityRouteKey = "id")]
    [RequirePermission(AdminPermissions.BillingPlansManage)]
    public async Task<ActionResult<PlanDto>> UpdatePlan(Guid id, [FromBody] PlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _planService.UpdatePlanAsync(id, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }


}

