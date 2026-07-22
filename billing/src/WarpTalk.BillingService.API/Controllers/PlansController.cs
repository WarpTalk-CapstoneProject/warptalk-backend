using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PlanDto>> GetPlanById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _planService.GetPlanByIdAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("slug/{slug}")]
    public async Task<ActionResult<PlanDto>> GetPlanBySlug(string slug, CancellationToken cancellationToken)
    {
        var result = await _planService.GetPlanBySlugAsync(slug, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPost]
    [Authorize]
    public async Task<ActionResult<PlanDto>> CreatePlan([FromBody] PlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _planService.CreatePlanAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return CreatedAtAction(nameof(GetPlanById), new { id = result.Value!.Id }, result.Value);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PlanDto>> UpdatePlan(Guid id, [FromBody] PlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _planService.UpdatePlanAsync(id, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeactivatePlan(Guid id, CancellationToken cancellationToken)
    {
        var result = await _planService.DeactivatePlanAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return NoContent();
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        errorCode switch
        {
            ErrorCodes.BillingPlanNotFound => NotFound(new { Message = error }),
            "DUPLICATE_SLUG" => BadRequest(new { Message = error }),
            "INVALID_REQUEST" => BadRequest(new { Message = error }),
            _ => StatusCode(500, new { Message = error })
        };
}
