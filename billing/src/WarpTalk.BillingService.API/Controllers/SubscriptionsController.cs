using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.BillingService.API.Filters;



namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class SubscriptionsController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;

    public SubscriptionsController(ISubscriptionService subscriptionService)
    {
        _subscriptionService = subscriptionService;
    }

    [HttpPost]
    public async Task<ActionResult<SubscriptionDto>> CreateSubscription([FromBody] SubscriptionRequest request, CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.CreateSubscriptionAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return StatusCode(201, result.Value);
    }

    [HttpGet("workspace/{workspaceId}")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<SubscriptionDto>> GetActiveSubscription(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.GetActiveSubscriptionAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("global")]
    [AllowAnonymous]
    public async Task<ActionResult<PaginatedResponse<SubscriptionDto>>> GetGlobalSubscriptions(
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _subscriptionService.GetGlobalSubscriptionsAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpDelete("workspace/{workspaceId}")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<IActionResult> CancelSubscription(Guid workspaceId, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.CancelSubscriptionAsync(workspaceId, reason, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return NoContent();
    }

    [HttpPut("workspace/{workspaceId}/change-plan")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<SubscriptionDto>> ChangeSubscription(Guid workspaceId, [FromBody] SubscriptionRequest request, CancellationToken cancellationToken)
    {
        if (workspaceId != request.WorkspaceId)
            return BadRequest(new ApiErrorResponse(ApiMessageConstants.ValidationMessages.WorkspaceIdMismatch, ErrorCodes.ValidationError));

        var result = await _subscriptionService.ChangeSubscriptionAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        errorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new ApiErrorResponse(error ?? ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, errorCode)),
            ErrorCodes.BillingSubscriptionAlreadyActive => Conflict(new ApiErrorResponse(error ?? ApiMessageConstants.ErrorMessages.BillingSubscriptionAlreadyActive, errorCode)),
            ErrorCodes.BillingPlanNotFound => BadRequest(new ApiErrorResponse(error ?? ApiMessageConstants.ErrorMessages.BillingPlanNotFound, errorCode)),
            _ => StatusCode(500, new ApiErrorResponse(error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, errorCode ?? ErrorCodes.InternalServerError))
        };
}
