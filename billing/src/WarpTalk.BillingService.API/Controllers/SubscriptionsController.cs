using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Extensions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;




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
        if (!result.IsSuccess)
        {
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return StatusCode(201, result.Value);
    }

    [HttpPost("trial")]
    public async Task<ActionResult<SubscriptionDto>> CreateTrialSubscription([FromBody] TrialSubscriptionRequest request, CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.CreateTrialSubscriptionAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return StatusCode(201, result.Value);
    }

    [HttpGet("workspace/{workspaceId}")]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdmin)]
    public async Task<ActionResult<SubscriptionDto>> GetActiveSubscription(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.GetActiveSubscriptionAsync(workspaceId, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("global")]
    [Authorize(Roles = WorkspaceRoleConstants.Admin)]
    public async Task<ActionResult<PaginatedResponse<SubscriptionDto>>> GetGlobalSubscriptions(
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _subscriptionService.GetGlobalSubscriptionsAsync(query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpDelete("workspace/{workspaceId}")]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdmin)]
    public async Task<IActionResult> CancelSubscription(Guid workspaceId, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.CancelSubscriptionAsync(workspaceId, reason, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return NoContent();
    }

    [HttpPut("workspace/{workspaceId}/change-plan")]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdmin)]
    public async Task<ActionResult<SubscriptionDto>> ChangeSubscription(Guid workspaceId, [FromBody] SubscriptionRequest request, CancellationToken cancellationToken)
    {
        if (workspaceId != request.WorkspaceId)
            return this.ToBadRequest(ApiMessageConstants.ValidationMessages.WorkspaceIdMismatch, ErrorCodes.ValidationError);

        var result = await _subscriptionService.ChangeSubscriptionAsync(request, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPost("workspace/{workspaceId}/resume")]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdmin)]
    public async Task<ActionResult<SubscriptionDto>> ResumeSubscription(
        Guid workspaceId,
        [FromBody] ResumeSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.ResumeSubscriptionAsync(workspaceId, request, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPut("workspace/{workspaceId}/contract-terms")]
    [Authorize(Roles = WorkspaceRoleConstants.Admin)]
    public async Task<ActionResult<SubscriptionDto>> UpdateContractTerms(
        Guid workspaceId,
        [FromBody] UpdateSubscriptionContractTermsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.UpdateContractTermsAsync(workspaceId, request, cancellationToken);
        return result.ToActionResult(this);
    }
}
