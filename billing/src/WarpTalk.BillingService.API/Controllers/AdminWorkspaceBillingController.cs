using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// The billing half of the admin workspace page (ERP-style detail): the money overview it leads
/// with, and the actions an operator takes on one tenant — adjust credits, change plan, extend a
/// trial, comp a period, override contract entitlements, mark an invoice paid.
///
/// Gated by the shared system-admin policy, never by a role string: <c>Roles = "Admin, admin"</c>
/// admits anybody holding the GLOBAL Admin row, which seeded sample accounts carry. Every write takes
/// a reason and is recorded in the platform audit log before it is saved; the actor comes from the
/// token, and the workspace from the route — no body can name either.
///
/// Routed beside <see cref="AdminWorkspaceAnalyticsController"/> under ~/api/v1/admin/billing,
/// because the gateway forwards ~/api/v1/admin/workspaces to the workspace service.
/// </summary>
[ApiController]
[Route("api/v1/admin/billing/workspaces/{workspaceId:guid}")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminWorkspaceBillingController : ControllerBase
{
    private readonly IAdminWorkspaceBillingService _service;

    public AdminWorkspaceBillingController(IAdminWorkspaceBillingService service)
    {
        _service = service;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(Guid workspaceId, [FromQuery] AdminDateRange range, CancellationToken ct)
        => ToActionResult(await _service.GetOverviewAsync(workspaceId, range, ct));

    [HttpPost("credits/adjust")]
    public Task<IActionResult> AdjustCredits(
        Guid workspaceId, [FromBody] AdminAdjustWorkspaceCreditsRequest request, CancellationToken ct)
        => ActAsync(actor => _service.AdjustCreditsAsync(workspaceId, request, actor, ct));

    [HttpPost("subscription/change-plan")]
    public Task<IActionResult> ChangePlan(
        Guid workspaceId, [FromBody] AdminWorkspaceChangePlanRequest request, CancellationToken ct)
        => ActAsync(actor => _service.ChangePlanAsync(workspaceId, request, actor, ct));

    [HttpPost("subscription/extend-trial")]
    public Task<IActionResult> ExtendTrial(
        Guid workspaceId, [FromBody] AdminExtendTrialRequest request, CancellationToken ct)
        => ActAsync(actor => _service.ExtendTrialAsync(workspaceId, request, actor, ct));

    [HttpPost("subscription/comp")]
    public Task<IActionResult> CompPeriod(
        Guid workspaceId, [FromBody] AdminCompPeriodRequest request, CancellationToken ct)
        => ActAsync(actor => _service.CompPeriodAsync(workspaceId, request, actor, ct));

    /// <summary>PUT: the body is the set of keys to change; keys it does not name are kept.</summary>
    [HttpPut("subscription/entitlements")]
    public Task<IActionResult> SetEntitlementOverrides(
        Guid workspaceId, [FromBody] AdminEntitlementOverridesRequest request, CancellationToken ct)
        => ActAsync(actor => _service.SetEntitlementOverridesAsync(workspaceId, request, actor, ct));

    /// <summary>
    /// The audited door to the long-standing mark-paid (<c>Invoice.MarkPaid</c>, the same code
    /// <c>POST /invoices/{id}/mark-paid</c> runs), scoped to this workspace and carrying a reason.
    /// </summary>
    [HttpPost("invoices/{invoiceId:guid}/mark-paid")]
    public Task<IActionResult> MarkInvoicePaid(
        Guid workspaceId, Guid invoiceId, [FromBody] AdminMarkInvoicePaidRequest request, CancellationToken ct)
        => ActAsync(actor => _service.MarkInvoicePaidAsync(workspaceId, invoiceId, request, actor, ct));

    private async Task<IActionResult> ActAsync<T>(Func<AdminActorContext, Task<Result<T>>> action)
    {
        if (!AdminActorContext.TryResolve(User, HttpContext, out var actor))
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        return ToActionResult(await action(actor));
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        var error = new ApiErrorResponse(result.Error, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(error),
            ErrorCodes.ValidationError => BadRequest(error),
            ErrorCodes.Conflict => Conflict(error),
            _ => StatusCode(500, error),
        };
    }
}
