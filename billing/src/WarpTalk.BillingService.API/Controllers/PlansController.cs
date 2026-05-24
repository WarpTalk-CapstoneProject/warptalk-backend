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

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            ErrorCodes.BillingPlanNotFound => NotFound(new { Message = result.Error }),
            _ => StatusCode(500, new { Message = result.Error })
        };
}
