using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
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

    [HttpPost("contract")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<SubscriptionDto>> CreateWorkspaceContractSubscription(
        [FromBody] CreateWorkspaceContractSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.CreateWorkspaceContractSubscriptionAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return StatusCode(201, result.Value);
    }

    [HttpPost("trial")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<SubscriptionDto>> CreateTrialSubscription([FromBody] TrialSubscriptionRequest request, CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.CreateTrialSubscriptionAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return StatusCode(201, result.Value);
    }

    [HttpGet("workspace/{workspaceId}")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<SubscriptionDto>> GetActiveSubscription(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.GetActiveSubscriptionAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpGet("global")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<PaginatedResponse<SubscriptionDto>>> GetGlobalSubscriptions(
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _subscriptionService.GetGlobalSubscriptionsAsync(query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpDelete("workspace/{workspaceId}")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<IActionResult> CancelSubscription(Guid workspaceId, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.CancelSubscriptionAsync(workspaceId, reason, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return NoContent();
    }

    [HttpPost("workspace/{workspaceId}/resume")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<SubscriptionDto>> ResumeSubscription(
        Guid workspaceId,
        [FromBody] ResumeSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.ResumeSubscriptionAsync(workspaceId, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpPut("workspace/{workspaceId}/contract-terms")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<SubscriptionDto>> UpdateContractTerms(
        Guid workspaceId,
        [FromBody] UpdateSubscriptionContractTermsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.UpdateContractTermsAsync(workspaceId, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Whether this workspace keeps translating past zero credits, and how far.
    ///
    /// Workspace-scoped, not [Authorize(AdminSystem)] like contract-terms above: the Owner is
    /// choosing whether to USE an allowance, not how big it is. The service refuses to enable
    /// anything on a plan whose cap is 0.
    /// </summary>
    [HttpGet("workspace/{workspaceId}/overage")]
    public async Task<ActionResult<WorkspaceOverageSettingDto>> GetOverage(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.GetOverageSettingAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpPut("workspace/{workspaceId}/overage")]
    public async Task<ActionResult<WorkspaceOverageSettingDto>> SetOverage(
        Guid workspaceId,
        [FromBody] SetWorkspaceOverageRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.SetOverageAsync(workspaceId, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }
}
