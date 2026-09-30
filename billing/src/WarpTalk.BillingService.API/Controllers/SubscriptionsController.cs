using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Events;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class SubscriptionsController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IStripeSubscriptionLifecycleService _lifecycle;

    public SubscriptionsController(ISubscriptionService subscriptionService, IStripeSubscriptionLifecycleService lifecycle)
    {
        _subscriptionService = subscriptionService;
        _lifecycle = lifecycle;
    }

    [HttpPost("contract")]
    [AdminAudited(AdminAuditBillingActions.ContractCreated, AdminAuditEntityTypes.Subscription, typeof(Subscription))]
    [RequirePermission(AdminPermissions.BillingSubscriptionsManage)]
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
    [RequirePermission(AdminPermissions.BillingRead)]
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
    [AdminAudited(AdminAuditBillingActions.SubscriptionCancelled, AdminAuditEntityTypes.Subscription, typeof(Subscription))]
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

    /// <summary>
    /// WT-471: undo a cancellation while the paid period is still running.
    ///
    /// Same role gate as cancel — whoever may cancel may reverse it. It creates no charge: the
    /// period is already paid for, and a workspace whose period has ended is told to choose a plan
    /// instead, because that is a Checkout flow.
    /// </summary>
    [HttpPost("workspace/{workspaceId}/reactivate")]
    [AdminAudited(AdminAuditBillingActions.SubscriptionReactivated, AdminAuditEntityTypes.Subscription, typeof(Subscription))]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<SubscriptionDto>> ReactivateSubscription(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionService.ReactivateSubscriptionAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(
                result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
                result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpPost("workspace/{workspaceId}/resume")]
    [AdminAudited(AdminAuditBillingActions.SubscriptionResumed, AdminAuditEntityTypes.Subscription, typeof(Subscription))]
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

    /// <summary>
    /// #466: switch automatic renewal off or back on. For a card plan this is Stripe's
    /// cancel_at_period_end — the paid period always runs to its end — and Stripe is asked first.
    /// A plan that was paid once (no card on file) answers 409 BILLING_AUTO_RENEW_REQUIRES_CHECKOUT
    /// when switched on: that needs a new checkout with auto-renew on.
    /// </summary>
    [HttpPut("workspace/{workspaceId}/auto-renew")]
    [AdminAudited(AdminAuditBillingActions.SubscriptionAutoRenewSet, AdminAuditEntityTypes.Subscription, typeof(Subscription))]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<SubscriptionDto>> SetAutoRenew(
        Guid workspaceId,
        [FromBody] SetAutoRenewRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _lifecycle.SetAutoRenewAsync(workspaceId, request.AutoRenew, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var error = new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(error),
            StripeSubscriptionLifecycleService.AutoRenewRequiresCheckoutCode => Conflict(error),
            ErrorCodes.BillingExternalServiceError => StatusCode(StatusCodes.Status502BadGateway, error),
            _ => BadRequest(error),
        };
    }

    /// <summary>
    /// #466: renewal as the billing page shows it — who renews, the next charge date and amount,
    /// the card on file (brand and last four digits only) and any failed renewal charge.
    /// </summary>
    [HttpGet("workspace/{workspaceId}/recurring")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<RecurringBillingStatusDto>> GetRecurringStatus(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _lifecycle.GetRecurringBillingStatusAsync(workspaceId, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var error = new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode);
        return result.ErrorCode == ErrorCodes.BillingSubscriptionNotFound ? NotFound(error) : BadRequest(error);
    }

    /// <summary>
    /// #466: a Stripe billing-portal link where the owner updates the card. Returns only the URL;
    /// the portal session is Stripe's and expires on its own. Writes nothing here.
    /// </summary>
    [HttpPost("workspace/{workspaceId}/billing-portal")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<BillingPortalDto>> CreateBillingPortal(
        Guid workspaceId,
        [FromBody] BillingPortalRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _lifecycle.CreateBillingPortalAsync(workspaceId, request?.ReturnPath, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var error = new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(error),
            ErrorCodes.BillingExternalServiceError => StatusCode(StatusCodes.Status502BadGateway, error),
            _ => BadRequest(error),
        };
    }

    [HttpPut("workspace/{workspaceId}/contract-terms")]
    [AdminAudited(AdminAuditBillingActions.ContractTermsUpdated, AdminAuditEntityTypes.Subscription, typeof(Subscription))]
    [RequirePermission(AdminPermissions.BillingSubscriptionsManage)]
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
    ///
    /// WT-699 / TC3405: both actions used to carry nothing but the class-level [Authorize], so any
    /// logged-in account — a Member, a guest, somebody from another workspace entirely — could read
    /// this workspace's overage cap and switch paid overage ON, committing the Owner to charges
    /// past zero credits. They now take the same workspace-role gate as every other money-moving
    /// workspace action on this controller (cancel, reactivate, resume) and as credit top-up:
    /// resolved from membership through workspace-service, never from JWT role claims (WT-260).
    /// The read is gated the same way because the billing page that renders it is Owner/Admin-only.
    /// </summary>
    [HttpGet("workspace/{workspaceId}/overage")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
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
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
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
