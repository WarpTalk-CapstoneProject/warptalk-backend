using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Extensions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
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
    public async Task<ActionResult<BillingPolicyDto>> GetBillingPolicy(CancellationToken cancellationToken)
    {
        var policy = await _billingPolicyService.GetPolicyAsync(cancellationToken);
        return Ok(policy);
    }

    [HttpPut]
    public async Task<ActionResult<BillingPolicyDto>> UpdateBillingPolicy(
        [FromBody] UpdateBillingPolicyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _billingPolicyService.UpdatePolicyAsync(request, cancellationToken);
        return result.ToActionResult(this);
    }
}

