using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

// Plans are public — no [Authorize] required.
[ApiController]
[Route("api/v1/plans")]
public class PlansController : ControllerBase
{
    private readonly IPlanService _planService;

    public PlansController(IPlanService planService)
    {
        _planService = planService;
    }

    /// <summary>
    /// List all currently active subscription plans.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<PlanDto>>> GetPlans(CancellationToken cancellationToken)
    {
        var result = await _planService.GetActivePlansAsync(cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Get a single plan by its ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PlanDto>> GetPlanById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _planService.GetPlanByIdAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Get a single plan by its URL slug.
    /// </summary>
    [HttpGet("slug/{slug}")]
    public async Task<ActionResult<PlanDto>> GetPlanBySlug(string slug, CancellationToken cancellationToken)
    {
        var result = await _planService.GetPlanBySlugAsync(slug, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Create a new plan (Admin only).
    /// </summary>
    [HttpPost]
    [Authorize]
    public async Task<ActionResult<PlanDto>> CreatePlan(
        [FromBody] PlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _planService.CreatePlanAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return CreatedAtAction(nameof(GetPlanById), new { id = result.Value!.Id }, result.Value);
    }

    /// <summary>
    /// Update an existing plan (Admin only).
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PlanDto>> UpdatePlan(
        Guid id, [FromBody] PlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _planService.UpdatePlanAsync(id, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Deactivate/soft-delete a plan (Admin only).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeactivatePlan(
        Guid id, CancellationToken cancellationToken)
    {
        var result = await _planService.DeactivatePlanAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return NoContent();
    }

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            ErrorCodes.BillingPlanNotFound => NotFound(new { Message = result.Error }),
            "DUPLICATE_SLUG" => BadRequest(new { Message = result.Error }),
            "INVALID_REQUEST" => BadRequest(new { Message = result.Error }),
            _ => StatusCode(500, new { Message = result.Error })
        };
}
