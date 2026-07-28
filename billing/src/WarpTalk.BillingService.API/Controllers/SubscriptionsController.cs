using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.BillingService.API.Filters;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/subscriptions")]
public class SubscriptionsController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;

    public SubscriptionsController(ISubscriptionService subscriptionService)
    {
        _subscriptionService = subscriptionService;
    }

    /// <summary>
    /// Provision a new subscription for a workspace.
    /// </summary>
    [HttpPost]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<SubscriptionDto>> CreateSubscription(
        [FromBody] SubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.CreateSubscriptionAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return StatusCode(201, result.Value);
    }

    /// <summary>
    /// Get the active subscription for a workspace.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<SubscriptionDto>> GetActiveSubscription(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.GetActiveSubscriptionAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Get paginated global subscriptions for admins.
    /// </summary>
    [HttpGet("global")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PagedResult<SubscriptionDto>>> GetGlobalSubscriptions(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _subscriptionService.GetGlobalSubscriptionsAsync(pageNumber, pageSize, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Cancel the active subscription for a workspace.
    /// </summary>
    [HttpDelete("workspace/{workspaceId:guid}")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<IActionResult> CancelSubscription(
        Guid workspaceId,
        [FromQuery] string? reason,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.CancelSubscriptionAsync(workspaceId, reason, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return NoContent();
    }

    /// <summary>
    /// Change the active subscription plan for a workspace.
    /// </summary>
    [HttpPut("workspace/{workspaceId:guid}/change-plan")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<SubscriptionDto>> ChangeSubscription(
        Guid workspaceId,
        [FromBody] SubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        if (workspaceId != request.WorkspaceId)
            return BadRequest(new { Message = "Workspace ID in URL does not match request body." });

        var result = await _subscriptionService.ChangeSubscriptionAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new { Message = result.Error }),
            ErrorCodes.BillingSubscriptionAlreadyActive => Conflict(new { Message = result.Error }),
            ErrorCodes.BillingPlanNotFound => BadRequest(new { Message = result.Error }),
            _ => StatusCode(500, new { Message = result.Error })
        };
}
