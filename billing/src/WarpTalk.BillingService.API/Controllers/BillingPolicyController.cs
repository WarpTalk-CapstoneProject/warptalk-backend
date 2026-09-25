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
[Route("api/v1/billing-policy")]
public class BillingPolicyController : ControllerBase
{
    private readonly IBillingPolicyService _billingPolicyService;

    public BillingPolicyController(IBillingPolicyService billingPolicyService)
    {
        _billingPolicyService = billingPolicyService;
    }

    [HttpGet]
    [RequirePermission(AdminPermissions.SettingsRead)]
    public async Task<ActionResult<BillingPolicyDto>> GetBillingPolicy(CancellationToken cancellationToken)
    {
        var policy = await _billingPolicyService.GetPolicyAsync(cancellationToken);
        return Ok(policy);
    }

    [HttpPut]
    [AdminAudited(AdminAuditBillingActions.BillingPolicyUpdated, AdminAuditEntityTypes.BillingPolicy, typeof(BillingPolicyConfig))]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<ActionResult<BillingPolicyDto>> UpdateBillingPolicy(
        [FromBody] UpdateBillingPolicyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _billingPolicyService.UpdatePolicyAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }
}

