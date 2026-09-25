using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// G11 — the customer half of the catalog: what a workspace may buy, what a coupon would take off,
/// and the server-side pricing of a catalog checkout. The price a customer pays is always decided
/// here, never taken from the request.
/// </summary>
public interface ICustomerCatalogService
{
    Task<Result<WorkspaceCatalogDto>> GetCatalogAsync(Guid workspaceId, CancellationToken ct = default);

    Task<Result<CouponPreviewDto>> PreviewCouponAsync(Guid workspaceId, CouponPreviewRequest request, CancellationToken ct = default);

    /// <summary>
    /// Prices a checkout of a credit pack or add-on (or applies a coupon to a plan checkout).
    /// Returns the request with the server's Amount/Currency and the extras the Stripe session
    /// needs — or a failure naming why this workspace may not buy it.
    /// </summary>
    Task<Result<(CreateCheckoutSessionRequest Request, CheckoutExtras Extras)>> PrepareCheckoutAsync(
        CreateCheckoutSessionRequest request,
        CancellationToken ct = default);

    Task<Result<WorkspaceAddonDto>> CancelAddonAsync(Guid workspaceId, Guid workspaceAddonId, CancellationToken ct = default);
}
