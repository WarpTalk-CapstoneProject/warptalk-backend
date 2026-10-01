using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// G11 — the customer side of the catalog, for the workspace billing page: the credit packs and
/// add-ons this workspace may buy, a coupon preview, and cancelling an add-on. Buying goes through
/// the existing POST /api/v1/payments/checkout (PaymentType CreditPack / AddOn), which prices the
/// item server-side.
///
/// Under /api/v1/payments, which the gateway already forwards to billing with RequireAuth. The
/// same roles as checkout: a workspace Owner or Admin (or a platform admin).
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/payments/workspace/{workspaceId:guid}")]
public class WorkspaceCatalogController : ControllerBase
{
    private readonly ICustomerCatalogService _catalog;

    public WorkspaceCatalogController(ICustomerCatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpGet("catalog")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<WorkspaceCatalogDto>> GetCatalog(Guid workspaceId, CancellationToken ct)
        => this.ToActionResult(await _catalog.GetCatalogAsync(workspaceId, ct));

    [HttpPost("coupon-preview")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<CouponPreviewDto>> PreviewCoupon(Guid workspaceId, [FromBody] CouponPreviewRequest request, CancellationToken ct)
        => this.ToActionResult(await _catalog.PreviewCouponAsync(workspaceId, request, ct));

    /// <summary>Cancels an add-on at the end of the period already paid for.</summary>
    [HttpPost("addons/{workspaceAddonId:guid}/cancel")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<WorkspaceAddonDto>> CancelAddon(Guid workspaceId, Guid workspaceAddonId, CancellationToken ct)
    {
        var result = await _catalog.CancelAddonAsync(workspaceId, workspaceAddonId, ct);
        if (!result.IsSuccess && result.ErrorCode == ErrorCodes.BillingExternalServiceError)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return this.ToActionResult(result);
    }
}
