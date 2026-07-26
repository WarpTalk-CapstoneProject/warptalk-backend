using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Extensions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

// Plans are public — no [Authorize] required.
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
        return result.ToActionResult(this);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PlanDto>> GetPlanById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _planService.GetPlanByIdAsync(id, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("slug/{slug}")]
    public async Task<ActionResult<PlanDto>> GetPlanBySlug(string slug, CancellationToken cancellationToken)
    {
        var result = await _planService.GetPlanBySlugAsync(slug, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPost]
    [Authorize(Roles = WorkspaceRoleConstants.Admin)]
    public async Task<ActionResult<PlanDto>> CreatePlan([FromBody] PlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _planService.CreatePlanAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return CreatedAtAction(nameof(GetPlanById), new { id = result.Value!.Id }, result.Value);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = WorkspaceRoleConstants.Admin)]
    public async Task<ActionResult<PlanDto>> UpdatePlan(Guid id, [FromBody] PlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _planService.UpdatePlanAsync(id, request, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = WorkspaceRoleConstants.Admin)]
    public async Task<ActionResult> DeactivatePlan(Guid id, CancellationToken cancellationToken)
    {
        var result = await _planService.DeactivatePlanAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return NoContent();
    }
}
