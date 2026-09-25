using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// G11 — /admin/packages: everything sold besides a base plan. Credit packs (one-off credits),
/// add-ons (recurring extras raising an enforced entitlement) and coupons, each with create, edit,
/// duplicate, archive/unarchive, Stripe sync and a Stripe drift check.
///
/// System-admin only, under ~/api/v1/admin/billing, which the gateway's admin-billing-route
/// catch-all already forwards to billing-service. Every write carries [AdminAudited]: the change
/// is recorded (with its before/after diff) before it commits, or it does not commit.
///
/// NOTE for the staff-RBAC work (G10): when <c>RequirePermission</c> lands on development, these
/// endpoints take a new <c>billing.packages_manage</c> permission for writes and
/// <c>billing.read</c> for reads, replacing the system-admin policy below.
/// </summary>
[ApiController]
[Route("api/v1/admin/billing/packages")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminPackagesController : ControllerBase
{
    private readonly IPackageCatalogService _catalog;

    public AdminPackagesController(IPackageCatalogService catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Entitlement keys an add-on may raise, currencies, Stripe minimums, whether Stripe is configured.</summary>
    [HttpGet("options")]
    public async Task<ActionResult<PackageCatalogOptionsDto>> GetOptions(CancellationToken ct)
        => Ok(await _catalog.GetOptionsAsync(ct));

    // ---- Credit packs -----------------------------------------------------------------------

    [HttpGet("credit-packs")]
    public async Task<ActionResult<IReadOnlyList<CreditPackDto>>> ListCreditPacks(CancellationToken ct)
        => Ok(await _catalog.ListCreditPacksAsync(ct));

    [HttpGet("credit-packs/{id:guid}")]
    public async Task<ActionResult<CreditPackDto>> GetCreditPack(Guid id, CancellationToken ct)
        => ToResult(await _catalog.GetCreditPackAsync(id, ct));

    [HttpPost("credit-packs")]
    [AdminAudited(AdminAuditPackageActions.CreditPackCreated, AdminAuditEntityTypes.CreditPack, typeof(CreditPack))]
    public async Task<ActionResult<CreditPackDto>> CreateCreditPack([FromBody] CreditPackRequest request, CancellationToken ct)
        => ToResult(await _catalog.CreateCreditPackAsync(request, User.GetUserId(), ct));

    [HttpPut("credit-packs/{id:guid}")]
    [AdminAudited(AdminAuditPackageActions.CreditPackUpdated, AdminAuditEntityTypes.CreditPack, typeof(CreditPack), EntityRouteKey = "id")]
    public async Task<ActionResult<CreditPackDto>> UpdateCreditPack(Guid id, [FromBody] CreditPackRequest request, CancellationToken ct)
        => ToResult(await _catalog.UpdateCreditPackAsync(id, request, User.GetUserId(), ct));

    [HttpPost("credit-packs/{id:guid}/duplicate")]
    [AdminAudited(AdminAuditPackageActions.CreditPackDuplicated, AdminAuditEntityTypes.CreditPack, typeof(CreditPack))]
    public async Task<ActionResult<CreditPackDto>> DuplicateCreditPack(Guid id, CancellationToken ct)
        => ToResult(await _catalog.DuplicateCreditPackAsync(id, User.GetUserId(), ct));

    [HttpPost("credit-packs/{id:guid}/archive")]
    [AdminAudited(AdminAuditPackageActions.CreditPackArchived, AdminAuditEntityTypes.CreditPack, typeof(CreditPack), EntityRouteKey = "id")]
    public async Task<ActionResult<CreditPackDto>> ArchiveCreditPack(Guid id, CancellationToken ct)
        => ToResult(await _catalog.SetCreditPackArchivedAsync(id, true, User.GetUserId(), ct));

    [HttpPost("credit-packs/{id:guid}/unarchive")]
    [AdminAudited(AdminAuditPackageActions.CreditPackUnarchived, AdminAuditEntityTypes.CreditPack, typeof(CreditPack), EntityRouteKey = "id")]
    public async Task<ActionResult<CreditPackDto>> UnarchiveCreditPack(Guid id, CancellationToken ct)
        => ToResult(await _catalog.SetCreditPackArchivedAsync(id, false, User.GetUserId(), ct));

    [HttpPost("credit-packs/{id:guid}/stripe-sync")]
    [AdminAudited(AdminAuditPackageActions.CreditPackStripeSynced, AdminAuditEntityTypes.CreditPack, typeof(CreditPack), EntityRouteKey = "id")]
    public async Task<ActionResult<CreditPackDto>> SyncCreditPack(Guid id, CancellationToken ct)
        => ToResult(await _catalog.SyncCreditPackToStripeAsync(id, ct));

    [HttpGet("credit-packs/{id:guid}/stripe-drift")]
    public async Task<ActionResult<StripeDriftDto>> CreditPackDrift(Guid id, CancellationToken ct)
        => ToResult(await _catalog.GetCreditPackDriftAsync(id, ct));

    // ---- Add-ons ----------------------------------------------------------------------------

    [HttpGet("addons")]
    public async Task<ActionResult<IReadOnlyList<AddonDto>>> ListAddons(CancellationToken ct)
        => Ok(await _catalog.ListAddonsAsync(ct));

    [HttpGet("addons/{id:guid}")]
    public async Task<ActionResult<AddonDto>> GetAddon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.GetAddonAsync(id, ct));

    [HttpPost("addons")]
    [AdminAudited(AdminAuditPackageActions.AddonCreated, AdminAuditEntityTypes.Addon, typeof(Addon))]
    public async Task<ActionResult<AddonDto>> CreateAddon([FromBody] AddonRequest request, CancellationToken ct)
        => ToResult(await _catalog.CreateAddonAsync(request, User.GetUserId(), ct));

    [HttpPut("addons/{id:guid}")]
    [AdminAudited(AdminAuditPackageActions.AddonUpdated, AdminAuditEntityTypes.Addon, typeof(Addon), EntityRouteKey = "id")]
    public async Task<ActionResult<AddonDto>> UpdateAddon(Guid id, [FromBody] AddonRequest request, CancellationToken ct)
        => ToResult(await _catalog.UpdateAddonAsync(id, request, User.GetUserId(), ct));

    [HttpPost("addons/{id:guid}/duplicate")]
    [AdminAudited(AdminAuditPackageActions.AddonDuplicated, AdminAuditEntityTypes.Addon, typeof(Addon))]
    public async Task<ActionResult<AddonDto>> DuplicateAddon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.DuplicateAddonAsync(id, User.GetUserId(), ct));

    [HttpPost("addons/{id:guid}/archive")]
    [AdminAudited(AdminAuditPackageActions.AddonArchived, AdminAuditEntityTypes.Addon, typeof(Addon), EntityRouteKey = "id")]
    public async Task<ActionResult<AddonDto>> ArchiveAddon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.SetAddonArchivedAsync(id, true, User.GetUserId(), ct));

    [HttpPost("addons/{id:guid}/unarchive")]
    [AdminAudited(AdminAuditPackageActions.AddonUnarchived, AdminAuditEntityTypes.Addon, typeof(Addon), EntityRouteKey = "id")]
    public async Task<ActionResult<AddonDto>> UnarchiveAddon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.SetAddonArchivedAsync(id, false, User.GetUserId(), ct));

    [HttpPost("addons/{id:guid}/stripe-sync")]
    [AdminAudited(AdminAuditPackageActions.AddonStripeSynced, AdminAuditEntityTypes.Addon, typeof(Addon), EntityRouteKey = "id")]
    public async Task<ActionResult<AddonDto>> SyncAddon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.SyncAddonToStripeAsync(id, ct));

    [HttpGet("addons/{id:guid}/stripe-drift")]
    public async Task<ActionResult<StripeDriftDto>> AddonDrift(Guid id, CancellationToken ct)
        => ToResult(await _catalog.GetAddonDriftAsync(id, ct));

    // ---- Coupons ----------------------------------------------------------------------------

    [HttpGet("coupons")]
    public async Task<ActionResult<IReadOnlyList<CouponDto>>> ListCoupons(CancellationToken ct)
        => Ok(await _catalog.ListCouponsAsync(ct));

    [HttpGet("coupons/{id:guid}")]
    public async Task<ActionResult<CouponDto>> GetCoupon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.GetCouponAsync(id, ct));

    [HttpPost("coupons")]
    [AdminAudited(AdminAuditPackageActions.CouponCreated, AdminAuditEntityTypes.Coupon, typeof(Coupon))]
    public async Task<ActionResult<CouponDto>> CreateCoupon([FromBody] CouponRequest request, CancellationToken ct)
        => ToResult(await _catalog.CreateCouponAsync(request, User.GetUserId(), ct));

    [HttpPut("coupons/{id:guid}")]
    [AdminAudited(AdminAuditPackageActions.CouponUpdated, AdminAuditEntityTypes.Coupon, typeof(Coupon), EntityRouteKey = "id")]
    public async Task<ActionResult<CouponDto>> UpdateCoupon(Guid id, [FromBody] CouponRequest request, CancellationToken ct)
        => ToResult(await _catalog.UpdateCouponAsync(id, request, User.GetUserId(), ct));

    [HttpPost("coupons/{id:guid}/duplicate")]
    [AdminAudited(AdminAuditPackageActions.CouponDuplicated, AdminAuditEntityTypes.Coupon, typeof(Coupon))]
    public async Task<ActionResult<CouponDto>> DuplicateCoupon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.DuplicateCouponAsync(id, User.GetUserId(), ct));

    [HttpPost("coupons/{id:guid}/archive")]
    [AdminAudited(AdminAuditPackageActions.CouponArchived, AdminAuditEntityTypes.Coupon, typeof(Coupon), EntityRouteKey = "id")]
    public async Task<ActionResult<CouponDto>> ArchiveCoupon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.SetCouponArchivedAsync(id, true, User.GetUserId(), ct));

    [HttpPost("coupons/{id:guid}/unarchive")]
    [AdminAudited(AdminAuditPackageActions.CouponUnarchived, AdminAuditEntityTypes.Coupon, typeof(Coupon), EntityRouteKey = "id")]
    public async Task<ActionResult<CouponDto>> UnarchiveCoupon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.SetCouponArchivedAsync(id, false, User.GetUserId(), ct));

    [HttpPost("coupons/{id:guid}/stripe-sync")]
    [AdminAudited(AdminAuditPackageActions.CouponStripeSynced, AdminAuditEntityTypes.Coupon, typeof(Coupon), EntityRouteKey = "id")]
    public async Task<ActionResult<CouponDto>> SyncCoupon(Guid id, CancellationToken ct)
        => ToResult(await _catalog.SyncCouponToStripeAsync(id, ct));

    [HttpGet("coupons/{id:guid}/stripe-drift")]
    public async Task<ActionResult<StripeDriftDto>> CouponDrift(Guid id, CancellationToken ct)
        => ToResult(await _catalog.GetCouponDriftAsync(id, ct));

    /// <summary>
    /// 400 for a request the rules refuse, 404 for an unknown id, 409 for a clash (slug/code in
    /// use, editing an archived item), 502 when Stripe refused, 503 when Stripe is not configured.
    /// </summary>
    internal ActionResult<T> ToResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        var error = new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(error),
            ErrorCodes.Conflict or ErrorCodes.InvalidState => Conflict(error),
            ErrorCodes.ValidationError => BadRequest(error),
            ErrorCodes.ServiceUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, error),
            ErrorCodes.BillingExternalServiceError => StatusCode(StatusCodes.Status502BadGateway, error),
            _ => StatusCode(StatusCodes.Status500InternalServerError, error),
        };
    }
}
