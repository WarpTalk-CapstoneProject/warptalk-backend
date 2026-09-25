using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// G11 — admin management of the sellable catalog beyond plans (/admin/packages). Every write is
/// validated here, server-side, whatever the UI already checked.
/// </summary>
public interface IPackageCatalogService
{
    Task<PackageCatalogOptionsDto> GetOptionsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CreditPackDto>> ListCreditPacksAsync(CancellationToken ct = default);
    Task<Result<CreditPackDto>> GetCreditPackAsync(Guid id, CancellationToken ct = default);
    Task<Result<CreditPackDto>> CreateCreditPackAsync(CreditPackRequest request, Guid? adminId, CancellationToken ct = default);
    Task<Result<CreditPackDto>> UpdateCreditPackAsync(Guid id, CreditPackRequest request, Guid? adminId, CancellationToken ct = default);
    Task<Result<CreditPackDto>> DuplicateCreditPackAsync(Guid id, Guid? adminId, CancellationToken ct = default);
    Task<Result<CreditPackDto>> SetCreditPackArchivedAsync(Guid id, bool archived, Guid? adminId, CancellationToken ct = default);

    Task<IReadOnlyList<AddonDto>> ListAddonsAsync(CancellationToken ct = default);
    Task<Result<AddonDto>> GetAddonAsync(Guid id, CancellationToken ct = default);
    Task<Result<AddonDto>> CreateAddonAsync(AddonRequest request, Guid? adminId, CancellationToken ct = default);
    Task<Result<AddonDto>> UpdateAddonAsync(Guid id, AddonRequest request, Guid? adminId, CancellationToken ct = default);
    Task<Result<AddonDto>> DuplicateAddonAsync(Guid id, Guid? adminId, CancellationToken ct = default);
    Task<Result<AddonDto>> SetAddonArchivedAsync(Guid id, bool archived, Guid? adminId, CancellationToken ct = default);

    Task<IReadOnlyList<CouponDto>> ListCouponsAsync(CancellationToken ct = default);
    Task<Result<CouponDto>> GetCouponAsync(Guid id, CancellationToken ct = default);
    Task<Result<CouponDto>> CreateCouponAsync(CouponRequest request, Guid? adminId, CancellationToken ct = default);
    Task<Result<CouponDto>> UpdateCouponAsync(Guid id, CouponRequest request, Guid? adminId, CancellationToken ct = default);
    Task<Result<CouponDto>> DuplicateCouponAsync(Guid id, Guid? adminId, CancellationToken ct = default);
    Task<Result<CouponDto>> SetCouponArchivedAsync(Guid id, bool archived, Guid? adminId, CancellationToken ct = default);

    Task<Result<CreditPackDto>> SyncCreditPackToStripeAsync(Guid id, CancellationToken ct = default);
    Task<Result<AddonDto>> SyncAddonToStripeAsync(Guid id, CancellationToken ct = default);
    Task<Result<CouponDto>> SyncCouponToStripeAsync(Guid id, CancellationToken ct = default);

    Task<Result<StripeDriftDto>> GetCreditPackDriftAsync(Guid id, CancellationToken ct = default);
    Task<Result<StripeDriftDto>> GetAddonDriftAsync(Guid id, CancellationToken ct = default);
    Task<Result<StripeDriftDto>> GetCouponDriftAsync(Guid id, CancellationToken ct = default);
}
