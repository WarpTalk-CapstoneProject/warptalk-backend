using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// G11 — /admin/packages. Credit packs, add-ons and coupons: create, edit, duplicate,
/// archive/unarchive, push to Stripe and compare against Stripe, each list row carrying what the
/// item has actually sold (purchase and redemption rows written by the payment path).
///
/// Archiving is the only removal. A sold item is referenced by purchases, add-on subscriptions and
/// redemptions, and by Stripe objects that must never be deleted; an archived item is simply no
/// longer offered, and its Stripe Product and Prices are deactivated.
/// </summary>
public sealed class PackageCatalogService : IPackageCatalogService
{
    private const int MaxListedIds = 500;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IStripeCatalogSync _stripe;
    private readonly ILogger<PackageCatalogService> _logger;
    private readonly TimeProvider _time;

    public PackageCatalogService(
        IUnitOfWork unitOfWork,
        IStripeCatalogSync stripe,
        ILogger<PackageCatalogService> logger,
        TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _stripe = stripe;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public Task<PackageCatalogOptionsDto> GetOptionsAsync(CancellationToken ct = default) =>
        Task.FromResult(new PackageCatalogOptionsDto(
            PackageCatalogConstants.AddonEntitlements.All,
            EntitlementConstants.Keys.NumericLimits,
            PackageCatalogConstants.Currencies.All,
            PackageCatalogConstants.Currencies.All
                .Select(c => new MoneyDto(c, PackageCatalogConstants.Currencies.MinimumCharge(c)))
                .ToList(),
            _stripe.IsConfigured));

    // ---- Credit packs -----------------------------------------------------------------------

    public async Task<IReadOnlyList<CreditPackDto>> ListCreditPacksAsync(CancellationToken ct = default)
    {
        var packs = await _unitOfWork.CreditPacks.GetAllAsync(ct);
        var sales = (await _unitOfWork.CreditPackPurchases.GetSalesAsync(ct)).ToDictionary(s => s.ItemId);
        return packs
            .OrderBy(p => p.Status == PackageCatalogConstants.Statuses.Archived)
            .ThenBy(p => p.SortOrder)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => ToDto(p, sales.GetValueOrDefault(p.Id)))
            .ToList();
    }

    public async Task<Result<CreditPackDto>> GetCreditPackAsync(Guid id, CancellationToken ct = default)
    {
        var pack = await _unitOfWork.CreditPacks.GetByIdAsync(id, ct);
        return pack is null
            ? NotFound<CreditPackDto>()
            : Result.Success(await ToDtoWithSalesAsync(pack, ct));
    }

    public async Task<Result<CreditPackDto>> CreateCreditPackAsync(CreditPackRequest request, Guid? adminId, CancellationToken ct = default)
    {
        var invalid = PackageCatalogRules.ValidateCreditPack(request) ?? await ValidatePackReferencesAsync(request, ct);
        if (invalid is not null) return Invalid<CreditPackDto>(invalid);
        if (await _unitOfWork.CreditPacks.SlugExistsAsync(request.Slug, null, ct))
            return Result.Failure<CreditPackDto>(string.Format(PackageCatalogConstants.Errors.SlugTaken, request.Slug), ErrorCodes.Conflict);

        var now = Now;
        var pack = new CreditPack { Id = Guid.NewGuid(), CreatedAt = now, CreatedBy = adminId };
        Apply(pack, request);
        pack.UpdatedAt = now;
        pack.UpdatedBy = adminId;

        await _unitOfWork.CreditPacks.AddAsync(pack, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(ToDto(pack, null));
    }

    public async Task<Result<CreditPackDto>> UpdateCreditPackAsync(Guid id, CreditPackRequest request, Guid? adminId, CancellationToken ct = default)
    {
        var pack = await _unitOfWork.CreditPacks.GetByIdAsync(id, ct);
        if (pack is null) return NotFound<CreditPackDto>();
        if (pack.Status == PackageCatalogConstants.Statuses.Archived)
            return Result.Failure<CreditPackDto>(PackageCatalogConstants.Errors.Archived, ErrorCodes.InvalidState);

        var invalid = PackageCatalogRules.ValidateCreditPack(request) ?? await ValidatePackReferencesAsync(request, ct);
        if (invalid is not null) return Invalid<CreditPackDto>(invalid);
        if (await _unitOfWork.CreditPacks.SlugExistsAsync(request.Slug, id, ct))
            return Result.Failure<CreditPackDto>(string.Format(PackageCatalogConstants.Errors.SlugTaken, request.Slug), ErrorCodes.Conflict);

        Apply(pack, request);
        pack.UpdatedAt = Now;
        pack.UpdatedBy = adminId;
        _unitOfWork.CreditPacks.Update(pack);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(await ToDtoWithSalesAsync(pack, ct));
    }

    public async Task<Result<CreditPackDto>> DuplicateCreditPackAsync(Guid id, Guid? adminId, CancellationToken ct = default)
    {
        var source = await _unitOfWork.CreditPacks.GetByIdAsync(id, ct);
        if (source is null) return NotFound<CreditPackDto>();

        var slugs = (await _unitOfWork.CreditPacks.GetAllAsync(ct)).Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);
        var now = Now;
        var copy = new CreditPack
        {
            Id = Guid.NewGuid(),
            Slug = PackageCatalogRules.CopySlug(source.Slug, slugs.Contains),
            Name = Truncate($"{source.Name} (copy)", PackageCatalogConstants.Limits.NameMaxLength),
            Description = source.Description,
            Credits = source.Credits,
            BonusCredits = source.BonusCredits,
            PriceVnd = source.PriceVnd,
            PriceUsd = source.PriceUsd,
            ValidityDays = source.ValidityDays,
            Visibility = source.Visibility,
            EligiblePlanIds = source.EligiblePlanIds.ToArray(),
            EligibleWorkspaceIds = source.EligibleWorkspaceIds.ToArray(),
            MaxPerWorkspace = source.MaxPerWorkspace,
            MaxTotal = source.MaxTotal,
            AvailableFrom = source.AvailableFrom,
            AvailableUntil = source.AvailableUntil,
            // A copy is a draft with no Stripe objects of its own: it must be reviewed before it
            // is sold, and it must never share a Product or Price with the original.
            Status = PackageCatalogConstants.Statuses.Draft,
            SortOrder = source.SortOrder + 1,
            CreatedAt = now,
            CreatedBy = adminId,
            UpdatedAt = now,
            UpdatedBy = adminId,
        };

        await _unitOfWork.CreditPacks.AddAsync(copy, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(ToDto(copy, null));
    }

    public async Task<Result<CreditPackDto>> SetCreditPackArchivedAsync(Guid id, bool archived, Guid? adminId, CancellationToken ct = default)
    {
        var pack = await _unitOfWork.CreditPacks.GetByIdAsync(id, ct);
        if (pack is null) return NotFound<CreditPackDto>();

        var isArchived = pack.Status == PackageCatalogConstants.Statuses.Archived;
        if (isArchived == archived) return Result.Success(await ToDtoWithSalesAsync(pack, ct));

        // Unarchiving returns the item as a DRAFT: it was taken off sale deliberately, and putting
        // it back on sale is a second, explicit decision.
        pack.Status = archived ? PackageCatalogConstants.Statuses.Archived : PackageCatalogConstants.Statuses.Draft;
        pack.ArchivedAt = archived ? Now : null;
        pack.UpdatedAt = Now;
        pack.UpdatedBy = adminId;

        // An archived item that Stripe knows about is deactivated there too (never deleted).
        if (archived && !string.IsNullOrWhiteSpace(pack.StripeProductId) && _stripe.IsConfigured)
        {
            await _stripe.SyncCreditPackAsync(pack, ct);
        }

        _unitOfWork.CreditPacks.Update(pack);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(await ToDtoWithSalesAsync(pack, ct));
    }

    public async Task<Result<CreditPackDto>> SyncCreditPackToStripeAsync(Guid id, CancellationToken ct = default)
    {
        var pack = await _unitOfWork.CreditPacks.GetByIdAsync(id, ct);
        if (pack is null) return NotFound<CreditPackDto>();
        if (!_stripe.IsConfigured) return StripeNotConfigured<CreditPackDto>();

        var error = await _stripe.SyncCreditPackAsync(pack, ct);
        _unitOfWork.CreditPacks.Update(pack);
        await _unitOfWork.SaveChangesAsync(ct);
        if (error is not null)
        {
            _logger.LogWarning("stripe_catalog_sync_failed: credit pack {PackId} ({Slug}): {Error}", pack.Id, pack.Slug, error);
            return Result.Failure<CreditPackDto>(error, ErrorCodes.BillingExternalServiceError);
        }

        return Result.Success(await ToDtoWithSalesAsync(pack, ct));
    }

    public async Task<Result<StripeDriftDto>> GetCreditPackDriftAsync(Guid id, CancellationToken ct = default)
    {
        var pack = await _unitOfWork.CreditPacks.GetByIdAsync(id, ct);
        if (pack is null) return NotFound<StripeDriftDto>();
        var state = PackageCatalogRules.SyncState(pack.StripeProductId, pack.StripeSyncError, pack.StripeSyncedHash, PackageCatalogRules.Fingerprint(pack));
        return await DriftAsync(PackageCatalogConstants.ItemTypes.CreditPack, pack.Id, state, pack.StripeProductId,
            () => _stripe.CompareCreditPackAsync(pack, ct));
    }

    // ---- Add-ons ----------------------------------------------------------------------------

    public async Task<IReadOnlyList<AddonDto>> ListAddonsAsync(CancellationToken ct = default)
    {
        var addons = await _unitOfWork.Addons.GetAllAsync(ct);
        var sales = (await _unitOfWork.WorkspaceAddons.GetSalesAsync(ct)).ToDictionary(s => s.ItemId);
        return addons
            .OrderBy(a => a.Status == PackageCatalogConstants.Statuses.Archived)
            .ThenBy(a => a.SortOrder)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => ToDto(a, sales.GetValueOrDefault(a.Id)))
            .ToList();
    }

    public async Task<Result<AddonDto>> GetAddonAsync(Guid id, CancellationToken ct = default)
    {
        var addon = await _unitOfWork.Addons.GetByIdAsync(id, ct);
        return addon is null ? NotFound<AddonDto>() : Result.Success(await ToDtoWithSalesAsync(addon, ct));
    }

    public async Task<Result<AddonDto>> CreateAddonAsync(AddonRequest request, Guid? adminId, CancellationToken ct = default)
    {
        var invalid = PackageCatalogRules.ValidateAddon(request) ?? await ValidatePlanIdsAsync(request.EligiblePlanIds, ct);
        if (invalid is not null) return Invalid<AddonDto>(invalid);
        if (await _unitOfWork.Addons.SlugExistsAsync(request.Slug, null, ct))
            return Result.Failure<AddonDto>(string.Format(PackageCatalogConstants.Errors.SlugTaken, request.Slug), ErrorCodes.Conflict);

        var now = Now;
        var addon = new Addon { Id = Guid.NewGuid(), CreatedAt = now, CreatedBy = adminId };
        Apply(addon, request);
        addon.UpdatedAt = now;
        addon.UpdatedBy = adminId;
        await _unitOfWork.Addons.AddAsync(addon, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(ToDto(addon, null));
    }

    public async Task<Result<AddonDto>> UpdateAddonAsync(Guid id, AddonRequest request, Guid? adminId, CancellationToken ct = default)
    {
        var addon = await _unitOfWork.Addons.GetByIdAsync(id, ct);
        if (addon is null) return NotFound<AddonDto>();
        if (addon.Status == PackageCatalogConstants.Statuses.Archived)
            return Result.Failure<AddonDto>(PackageCatalogConstants.Errors.Archived, ErrorCodes.InvalidState);

        var invalid = PackageCatalogRules.ValidateAddon(request) ?? await ValidatePlanIdsAsync(request.EligiblePlanIds, ct);
        if (invalid is not null) return Invalid<AddonDto>(invalid);
        if (await _unitOfWork.Addons.SlugExistsAsync(request.Slug, id, ct))
            return Result.Failure<AddonDto>(string.Format(PackageCatalogConstants.Errors.SlugTaken, request.Slug), ErrorCodes.Conflict);

        // The entitlement an add-on raises is what live subscribers are paying for. Changing it
        // under them would silently swap one purchase for another, so it is fixed once sold.
        if (!string.Equals(addon.EntitlementKey, request.EntitlementKey, StringComparison.Ordinal)
            || addon.UnitsPerQuantity != request.UnitsPerQuantity)
        {
            var sales = (await _unitOfWork.WorkspaceAddons.GetSalesAsync(ct)).FirstOrDefault(s => s.ItemId == id);
            if (sales is { UnitsSold: > 0 })
            {
                return Invalid<AddonDto>(
                    "This add-on has been sold; its entitlement and units per quantity are fixed. Duplicate it to sell a different grant.");
            }
        }

        Apply(addon, request);
        addon.UpdatedAt = Now;
        addon.UpdatedBy = adminId;
        _unitOfWork.Addons.Update(addon);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(await ToDtoWithSalesAsync(addon, ct));
    }

    public async Task<Result<AddonDto>> DuplicateAddonAsync(Guid id, Guid? adminId, CancellationToken ct = default)
    {
        var source = await _unitOfWork.Addons.GetByIdAsync(id, ct);
        if (source is null) return NotFound<AddonDto>();

        var slugs = (await _unitOfWork.Addons.GetAllAsync(ct)).Select(a => a.Slug).ToHashSet(StringComparer.Ordinal);
        var now = Now;
        var copy = new Addon
        {
            Id = Guid.NewGuid(),
            Slug = PackageCatalogRules.CopySlug(source.Slug, slugs.Contains),
            Name = Truncate($"{source.Name} (copy)", PackageCatalogConstants.Limits.NameMaxLength),
            Description = source.Description,
            UnitLabel = source.UnitLabel,
            EntitlementKey = source.EntitlementKey,
            UnitsPerQuantity = source.UnitsPerQuantity,
            PriceMonthlyVnd = source.PriceMonthlyVnd,
            PriceYearlyVnd = source.PriceYearlyVnd,
            PriceMonthlyUsd = source.PriceMonthlyUsd,
            PriceYearlyUsd = source.PriceYearlyUsd,
            MinQuantity = source.MinQuantity,
            MaxQuantity = source.MaxQuantity,
            EligiblePlanIds = source.EligiblePlanIds.ToArray(),
            Status = PackageCatalogConstants.Statuses.Draft,
            SortOrder = source.SortOrder + 1,
            CreatedAt = now,
            CreatedBy = adminId,
            UpdatedAt = now,
            UpdatedBy = adminId,
        };
        await _unitOfWork.Addons.AddAsync(copy, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(ToDto(copy, null));
    }

    public async Task<Result<AddonDto>> SetAddonArchivedAsync(Guid id, bool archived, Guid? adminId, CancellationToken ct = default)
    {
        var addon = await _unitOfWork.Addons.GetByIdAsync(id, ct);
        if (addon is null) return NotFound<AddonDto>();

        var isArchived = addon.Status == PackageCatalogConstants.Statuses.Archived;
        if (isArchived == archived) return Result.Success(await ToDtoWithSalesAsync(addon, ct));

        // Archiving stops new sales. Workspaces already subscribed keep their add-on until they
        // cancel it: they paid for a period, and taking it back is a refund decision, not a
        // catalog one.
        addon.Status = archived ? PackageCatalogConstants.Statuses.Archived : PackageCatalogConstants.Statuses.Draft;
        addon.ArchivedAt = archived ? Now : null;
        addon.UpdatedAt = Now;
        addon.UpdatedBy = adminId;

        if (archived && !string.IsNullOrWhiteSpace(addon.StripeProductId) && _stripe.IsConfigured)
        {
            await _stripe.SyncAddonAsync(addon, ct);
        }

        _unitOfWork.Addons.Update(addon);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(await ToDtoWithSalesAsync(addon, ct));
    }

    public async Task<Result<AddonDto>> SyncAddonToStripeAsync(Guid id, CancellationToken ct = default)
    {
        var addon = await _unitOfWork.Addons.GetByIdAsync(id, ct);
        if (addon is null) return NotFound<AddonDto>();
        if (!_stripe.IsConfigured) return StripeNotConfigured<AddonDto>();

        var error = await _stripe.SyncAddonAsync(addon, ct);
        _unitOfWork.Addons.Update(addon);
        await _unitOfWork.SaveChangesAsync(ct);
        if (error is not null)
        {
            _logger.LogWarning("stripe_catalog_sync_failed: add-on {AddonId} ({Slug}): {Error}", addon.Id, addon.Slug, error);
            return Result.Failure<AddonDto>(error, ErrorCodes.BillingExternalServiceError);
        }

        return Result.Success(await ToDtoWithSalesAsync(addon, ct));
    }

    public async Task<Result<StripeDriftDto>> GetAddonDriftAsync(Guid id, CancellationToken ct = default)
    {
        var addon = await _unitOfWork.Addons.GetByIdAsync(id, ct);
        if (addon is null) return NotFound<StripeDriftDto>();
        var state = PackageCatalogRules.SyncState(addon.StripeProductId, addon.StripeSyncError, addon.StripeSyncedHash, PackageCatalogRules.Fingerprint(addon));
        return await DriftAsync(PackageCatalogConstants.ItemTypes.Addon, addon.Id, state, addon.StripeProductId,
            () => _stripe.CompareAddonAsync(addon, ct));
    }

    // ---- Coupons ----------------------------------------------------------------------------

    public async Task<IReadOnlyList<CouponDto>> ListCouponsAsync(CancellationToken ct = default)
    {
        var coupons = await _unitOfWork.Coupons.GetAllAsync(ct);
        var sales = (await _unitOfWork.CouponRedemptions.GetSalesAsync(ct)).ToDictionary(s => s.ItemId);
        return coupons
            .OrderBy(c => c.Status == PackageCatalogConstants.Statuses.Archived)
            .ThenByDescending(c => c.CreatedAt)
            .Select(c => ToDto(c, sales.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public async Task<Result<CouponDto>> GetCouponAsync(Guid id, CancellationToken ct = default)
    {
        var coupon = await _unitOfWork.Coupons.GetByIdAsync(id, ct);
        return coupon is null ? NotFound<CouponDto>() : Result.Success(await ToDtoWithSalesAsync(coupon, ct));
    }

    public async Task<Result<CouponDto>> CreateCouponAsync(CouponRequest request, Guid? adminId, CancellationToken ct = default)
    {
        var invalid = PackageCatalogRules.ValidateCoupon(request) ?? await ValidateCouponTargetsAsync(request, ct);
        if (invalid is not null) return Invalid<CouponDto>(invalid);

        var code = CodeOrNull(request.Code);
        if (code is not null && await _unitOfWork.Coupons.CodeExistsAsync(code, null, ct))
            return Result.Failure<CouponDto>(string.Format(PackageCatalogConstants.Errors.CodeTaken, code), ErrorCodes.Conflict);

        var now = Now;
        var coupon = new Coupon { Id = Guid.NewGuid(), CreatedAt = now, CreatedBy = adminId };
        Apply(coupon, request);
        coupon.UpdatedAt = now;
        coupon.UpdatedBy = adminId;
        await _unitOfWork.Coupons.AddAsync(coupon, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(ToDto(coupon, null));
    }

    public async Task<Result<CouponDto>> UpdateCouponAsync(Guid id, CouponRequest request, Guid? adminId, CancellationToken ct = default)
    {
        var coupon = await _unitOfWork.Coupons.GetByIdAsync(id, ct);
        if (coupon is null) return NotFound<CouponDto>();
        if (coupon.Status == PackageCatalogConstants.Statuses.Archived)
            return Result.Failure<CouponDto>(PackageCatalogConstants.Errors.Archived, ErrorCodes.InvalidState);

        var invalid = PackageCatalogRules.ValidateCoupon(request) ?? await ValidateCouponTargetsAsync(request, ct);
        if (invalid is not null) return Invalid<CouponDto>(invalid);

        var code = CodeOrNull(request.Code);
        if (code is not null && await _unitOfWork.Coupons.CodeExistsAsync(code, id, ct))
            return Result.Failure<CouponDto>(string.Format(PackageCatalogConstants.Errors.CodeTaken, code), ErrorCodes.Conflict);

        // A redeemed coupon's discount is part of receipts already issued; changing what it takes
        // off would make those receipts disagree with the coupon that produced them.
        var redemptions = await _unitOfWork.CouponRedemptions.CountForCouponAsync(id, ct);
        if (redemptions > 0 && (request.DiscountType != coupon.DiscountType
                                || request.PercentOff != coupon.PercentOff
                                || request.AmountOff != coupon.AmountOff
                                || !string.Equals(PackageCatalogConstants.Currencies.Normalize(request.AmountOffCurrency), coupon.AmountOffCurrency, StringComparison.Ordinal)
                                || request.Duration != coupon.Duration
                                || request.DurationInMonths != coupon.DurationInMonths))
        {
            return Invalid<CouponDto>(
                "This coupon has been redeemed; its discount and duration are fixed. Archive it and create a new one.");
        }

        Apply(coupon, request);
        coupon.UpdatedAt = Now;
        coupon.UpdatedBy = adminId;
        _unitOfWork.Coupons.Update(coupon);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(await ToDtoWithSalesAsync(coupon, ct));
    }

    public async Task<Result<CouponDto>> DuplicateCouponAsync(Guid id, Guid? adminId, CancellationToken ct = default)
    {
        var source = await _unitOfWork.Coupons.GetByIdAsync(id, ct);
        if (source is null) return NotFound<CouponDto>();

        string? code = null;
        if (source.Code is { Length: > 0 })
        {
            var codes = (await _unitOfWork.Coupons.GetAllAsync(ct)).Where(c => c.Code != null).Select(c => c.Code!).ToHashSet(StringComparer.Ordinal);
            var stem = source.Code.Length > PackageCatalogConstants.Limits.CodeMaxLength - 6
                ? source.Code[..(PackageCatalogConstants.Limits.CodeMaxLength - 6)]
                : source.Code;
            code = $"{stem}-COPY";
            for (var n = 2; codes.Contains(code); n++) code = $"{stem}-COPY{n}";
        }

        var now = Now;
        var copy = new Coupon
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = Truncate($"{source.Name} (copy)", PackageCatalogConstants.Limits.NameMaxLength),
            DiscountType = source.DiscountType,
            PercentOff = source.PercentOff,
            AmountOff = source.AmountOff,
            AmountOffCurrency = source.AmountOffCurrency,
            AppliesToTypes = source.AppliesToTypes.ToArray(),
            AppliesToIds = source.AppliesToIds.ToArray(),
            Duration = source.Duration,
            DurationInMonths = source.DurationInMonths,
            MaxRedemptions = source.MaxRedemptions,
            PerWorkspaceLimit = source.PerWorkspaceLimit,
            ValidFrom = source.ValidFrom,
            ValidUntil = source.ValidUntil,
            AutoApply = source.AutoApply,
            Status = PackageCatalogConstants.Statuses.Draft,
            CreatedAt = now,
            CreatedBy = adminId,
            UpdatedAt = now,
            UpdatedBy = adminId,
        };
        await _unitOfWork.Coupons.AddAsync(copy, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(ToDto(copy, null));
    }

    public async Task<Result<CouponDto>> SetCouponArchivedAsync(Guid id, bool archived, Guid? adminId, CancellationToken ct = default)
    {
        var coupon = await _unitOfWork.Coupons.GetByIdAsync(id, ct);
        if (coupon is null) return NotFound<CouponDto>();

        var isArchived = coupon.Status == PackageCatalogConstants.Statuses.Archived;
        if (isArchived == archived) return Result.Success(await ToDtoWithSalesAsync(coupon, ct));

        coupon.Status = archived ? PackageCatalogConstants.Statuses.Archived : PackageCatalogConstants.Statuses.Draft;
        coupon.ArchivedAt = archived ? Now : null;
        coupon.UpdatedAt = Now;
        coupon.UpdatedBy = adminId;

        // Archiving deactivates the promotion code so the code stops working in Stripe as well.
        // Discounts already running on subscriptions keep running — Stripe applies a coupon to the
        // invoices it was attached for, which is what the customer was promised.
        if (archived && !string.IsNullOrWhiteSpace(coupon.StripeCouponId) && _stripe.IsConfigured)
        {
            await _stripe.SyncCouponAsync(coupon, await CouponProductIdsAsync(coupon, ct), ct);
        }

        _unitOfWork.Coupons.Update(coupon);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(await ToDtoWithSalesAsync(coupon, ct));
    }

    public async Task<Result<CouponDto>> SyncCouponToStripeAsync(Guid id, CancellationToken ct = default)
    {
        var coupon = await _unitOfWork.Coupons.GetByIdAsync(id, ct);
        if (coupon is null) return NotFound<CouponDto>();
        if (!_stripe.IsConfigured) return StripeNotConfigured<CouponDto>();

        var unsynced = await UnsyncedCouponTargetsAsync(coupon, ct);
        if (unsynced.Count > 0)
        {
            return Invalid<CouponDto>(
                $"This coupon is limited to items Stripe does not know yet. Sync them first: {string.Join(", ", unsynced)}.");
        }

        var error = await _stripe.SyncCouponAsync(coupon, await CouponProductIdsAsync(coupon, ct), ct);
        _unitOfWork.Coupons.Update(coupon);
        await _unitOfWork.SaveChangesAsync(ct);
        if (error is not null)
        {
            _logger.LogWarning("stripe_catalog_sync_failed: coupon {CouponId}: {Error}", coupon.Id, error);
            return Result.Failure<CouponDto>(error, ErrorCodes.BillingExternalServiceError);
        }

        return Result.Success(await ToDtoWithSalesAsync(coupon, ct));
    }

    public async Task<Result<StripeDriftDto>> GetCouponDriftAsync(Guid id, CancellationToken ct = default)
    {
        var coupon = await _unitOfWork.Coupons.GetByIdAsync(id, ct);
        if (coupon is null) return NotFound<StripeDriftDto>();
        var state = PackageCatalogRules.SyncState(coupon.StripeCouponId, coupon.StripeSyncError, coupon.StripeSyncedHash, PackageCatalogRules.Fingerprint(coupon));
        return await DriftAsync("coupon", coupon.Id, state, coupon.StripeCouponId,
            () => _stripe.CompareCouponAsync(coupon, ct));
    }

    /// <summary>
    /// A coupon limited to specific packs/add-ons is limited to their Stripe Products on Stripe's
    /// side too (<c>applies_to.products</c>). Plans are not Stripe Products here (plan checkout
    /// uses inline price data), so a plan-targeted coupon cannot be product-limited in Stripe; our
    /// own server check is what enforces it.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> CouponProductIdsAsync(Coupon coupon, CancellationToken ct)
    {
        if (coupon.AppliesToIds.Length == 0) return [];
        var ids = coupon.AppliesToIds;
        var packs = await _unitOfWork.CreditPacks.FindAsync(p => ids.Contains(p.Id), ct);
        var addons = await _unitOfWork.Addons.FindAsync(a => ids.Contains(a.Id), ct);
        return packs.Select(p => p.StripeProductId)
            .Concat(addons.Select(a => a.StripeProductId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();
    }

    /// <summary>Names of the packs/add-ons a coupon is limited to that have no Stripe Product.</summary>
    private async Task<IReadOnlyList<string>> UnsyncedCouponTargetsAsync(Coupon coupon, CancellationToken ct)
    {
        if (coupon.AppliesToIds.Length == 0) return [];
        var ids = coupon.AppliesToIds;
        var packs = await _unitOfWork.CreditPacks.FindAsync(p => ids.Contains(p.Id), ct);
        var addons = await _unitOfWork.Addons.FindAsync(a => ids.Contains(a.Id), ct);
        return packs.Where(p => string.IsNullOrWhiteSpace(p.StripeProductId)).Select(p => p.Name)
            .Concat(addons.Where(a => string.IsNullOrWhiteSpace(a.StripeProductId)).Select(a => a.Name))
            .ToList();
    }

    // ---- Shared -----------------------------------------------------------------------------

    private async Task<Result<StripeDriftDto>> DriftAsync(
        string itemType,
        Guid itemId,
        string state,
        string? stripeObjectId,
        Func<Task<IReadOnlyList<StripeDriftItemDto>>> compare)
    {
        if (string.IsNullOrWhiteSpace(stripeObjectId))
        {
            return Result.Success(new StripeDriftDto(itemType, itemId, state, _stripe.IsConfigured, Now, [], null));
        }

        if (!_stripe.IsConfigured)
        {
            return Result.Success(new StripeDriftDto(itemType, itemId, state, false, Now, [], PackageCatalogConstants.Errors.StripeNotConfigured));
        }

        try
        {
            var differences = await compare();
            return Result.Success(new StripeDriftDto(itemType, itemId, state, true, Now, differences, null));
        }
        catch (Exception ex)
        {
            // The comparison is read-only and advisory: a Stripe outage is reported on the panel,
            // not turned into an error page.
            _logger.LogWarning(ex, "stripe_catalog_drift_failed: {ItemType} {ItemId}", itemType, itemId);
            return Result.Success(new StripeDriftDto(itemType, itemId, state, false, Now, [], ex.Message));
        }
    }

    private async Task<string?> ValidatePackReferencesAsync(CreditPackRequest request, CancellationToken ct)
    {
        if ((request.EligibleWorkspaceIds?.Count ?? 0) > MaxListedIds || (request.EligiblePlanIds?.Count ?? 0) > MaxListedIds)
            return $"At most {MaxListedIds} plans or workspaces can be listed.";
        if (request.EligibleWorkspaceIds?.Any(id => id == Guid.Empty) == true)
            return "A listed workspace id is empty.";
        return request.Visibility == PackageCatalogConstants.Visibility.Plans
            ? await ValidatePlanIdsAsync(request.EligiblePlanIds, ct)
            : null;
    }

    private async Task<string?> ValidatePlanIdsAsync(IReadOnlyList<Guid>? planIds, CancellationToken ct)
    {
        if (planIds is not { Count: > 0 }) return null;
        if (planIds.Count > MaxListedIds) return $"At most {MaxListedIds} plans can be listed.";
        var wanted = planIds.Distinct().ToArray();
        var found = await _unitOfWork.Plans.FindAsync(p => wanted.Contains(p.Id), ct);
        return found.Count == wanted.Length ? null : "One of the chosen plans does not exist.";
    }

    private async Task<string?> ValidateCouponTargetsAsync(CouponRequest request, CancellationToken ct)
    {
        if (request.AppliesToIds is not { Count: > 0 }) return null;
        if (request.AppliesToIds.Count > MaxListedIds) return $"At most {MaxListedIds} items can be listed.";

        var ids = request.AppliesToIds.Distinct().ToArray();
        var known = new HashSet<Guid>();
        if (request.AppliesToTypes.Contains(PackageCatalogConstants.ItemTypes.Plan))
            known.UnionWith((await _unitOfWork.Plans.FindAsync(p => ids.Contains(p.Id), ct)).Select(p => p.Id));
        if (request.AppliesToTypes.Contains(PackageCatalogConstants.ItemTypes.CreditPack))
            known.UnionWith((await _unitOfWork.CreditPacks.FindAsync(p => ids.Contains(p.Id), ct)).Select(p => p.Id));
        if (request.AppliesToTypes.Contains(PackageCatalogConstants.ItemTypes.Addon))
            known.UnionWith((await _unitOfWork.Addons.FindAsync(a => ids.Contains(a.Id), ct)).Select(a => a.Id));

        return ids.All(known.Contains)
            ? null
            : "Every item a coupon is limited to must be a plan, credit pack or add-on of a type it applies to.";
    }

    private static void Apply(CreditPack pack, CreditPackRequest request)
    {
        pack.Slug = request.Slug.Trim();
        pack.Name = request.Name.Trim();
        pack.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        pack.Credits = request.Credits;
        pack.BonusCredits = request.BonusCredits;
        pack.PriceVnd = request.PriceVnd;
        pack.PriceUsd = request.PriceUsd;
        pack.ValidityDays = request.ValidityDays;
        pack.Visibility = request.Visibility;
        // Only the list the visibility reads is kept, so a pack switched back to public does not
        // carry a stale restriction that would silently apply if it were switched again.
        pack.EligiblePlanIds = request.Visibility == PackageCatalogConstants.Visibility.Plans
            ? (request.EligiblePlanIds ?? []).Distinct().ToArray()
            : [];
        pack.EligibleWorkspaceIds = request.Visibility == PackageCatalogConstants.Visibility.Workspaces
            ? (request.EligibleWorkspaceIds ?? []).Distinct().ToArray()
            : [];
        pack.MaxPerWorkspace = request.MaxPerWorkspace;
        pack.MaxTotal = request.MaxTotal;
        pack.AvailableFrom = Utc(request.AvailableFrom);
        pack.AvailableUntil = Utc(request.AvailableUntil);
        pack.Status = request.Status;
        pack.SortOrder = request.SortOrder;
    }

    private static void Apply(Addon addon, AddonRequest request)
    {
        addon.Slug = request.Slug.Trim();
        addon.Name = request.Name.Trim();
        addon.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        addon.UnitLabel = request.UnitLabel.Trim();
        addon.EntitlementKey = request.EntitlementKey;
        addon.UnitsPerQuantity = request.UnitsPerQuantity;
        addon.PriceMonthlyVnd = request.PriceMonthlyVnd;
        addon.PriceYearlyVnd = request.PriceYearlyVnd;
        addon.PriceMonthlyUsd = request.PriceMonthlyUsd;
        addon.PriceYearlyUsd = request.PriceYearlyUsd;
        addon.MinQuantity = request.MinQuantity;
        addon.MaxQuantity = request.MaxQuantity;
        addon.EligiblePlanIds = (request.EligiblePlanIds ?? []).Distinct().ToArray();
        addon.Status = request.Status;
        addon.SortOrder = request.SortOrder;
    }

    private static void Apply(Coupon coupon, CouponRequest request)
    {
        coupon.Code = CodeOrNull(request.Code);
        coupon.Name = request.Name.Trim();
        coupon.DiscountType = request.DiscountType;
        coupon.PercentOff = request.DiscountType == PackageCatalogConstants.DiscountTypes.Percent ? request.PercentOff : null;
        coupon.AmountOff = request.DiscountType == PackageCatalogConstants.DiscountTypes.Fixed ? request.AmountOff : null;
        coupon.AmountOffCurrency = request.DiscountType == PackageCatalogConstants.DiscountTypes.Fixed
            ? PackageCatalogConstants.Currencies.Normalize(request.AmountOffCurrency)
            : null;
        coupon.AppliesToTypes = request.AppliesToTypes.Distinct(StringComparer.Ordinal).ToArray();
        coupon.AppliesToIds = (request.AppliesToIds ?? []).Distinct().ToArray();
        coupon.Duration = request.Duration;
        coupon.DurationInMonths = request.Duration == PackageCatalogConstants.Durations.Repeating ? request.DurationInMonths : null;
        coupon.MaxRedemptions = request.MaxRedemptions;
        coupon.PerWorkspaceLimit = request.PerWorkspaceLimit;
        coupon.ValidFrom = Utc(request.ValidFrom);
        coupon.ValidUntil = Utc(request.ValidUntil);
        coupon.AutoApply = request.AutoApply;
        coupon.Status = request.Status;
    }

    private static string? CodeOrNull(string? code)
    {
        var normalized = PackageCatalogRules.NormalizeCode(code);
        return normalized.Length == 0 ? null : normalized;
    }

    private static DateTime? Utc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } utc => utc,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        { } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private async Task<CreditPackDto> ToDtoWithSalesAsync(CreditPack pack, CancellationToken ct) =>
        ToDto(pack, (await _unitOfWork.CreditPackPurchases.GetSalesAsync(ct)).FirstOrDefault(s => s.ItemId == pack.Id));

    private async Task<AddonDto> ToDtoWithSalesAsync(Addon addon, CancellationToken ct) =>
        ToDto(addon, (await _unitOfWork.WorkspaceAddons.GetSalesAsync(ct)).FirstOrDefault(s => s.ItemId == addon.Id));

    private async Task<CouponDto> ToDtoWithSalesAsync(Coupon coupon, CancellationToken ct) =>
        ToDto(coupon, (await _unitOfWork.CouponRedemptions.GetSalesAsync(ct)).FirstOrDefault(s => s.ItemId == coupon.Id));

    internal static CreditPackDto ToDto(CreditPack pack, CatalogItemSales? sales) => new(
        pack.Id, pack.Slug, pack.Name, pack.Description, pack.Credits, pack.BonusCredits,
        pack.PriceVnd, pack.PriceUsd, pack.ValidityDays, pack.Visibility,
        pack.EligiblePlanIds, pack.EligibleWorkspaceIds, pack.MaxPerWorkspace, pack.MaxTotal,
        pack.AvailableFrom, pack.AvailableUntil, pack.Status, pack.SortOrder,
        StripeStatus(pack, PackageCatalogRules.Fingerprint(pack)),
        sales?.UnitsSold ?? 0,
        Money(sales),
        pack.CreatedAt, pack.UpdatedAt, pack.ArchivedAt);

    internal static AddonDto ToDto(Addon addon, CatalogItemSales? sales) => new(
        addon.Id, addon.Slug, addon.Name, addon.Description, addon.UnitLabel, addon.EntitlementKey,
        addon.UnitsPerQuantity, addon.PriceMonthlyVnd, addon.PriceYearlyVnd, addon.PriceMonthlyUsd,
        addon.PriceYearlyUsd, addon.MinQuantity, addon.MaxQuantity, addon.EligiblePlanIds,
        addon.Status, addon.SortOrder,
        StripeStatus(addon, PackageCatalogRules.Fingerprint(addon)),
        sales?.UnitsSold ?? 0,
        sales?.ActiveSubscribers ?? 0,
        sales?.ActiveQuantity ?? 0,
        Money(sales),
        addon.CreatedAt, addon.UpdatedAt, addon.ArchivedAt);

    internal static CouponDto ToDto(Coupon coupon, CatalogItemSales? sales) => new(
        coupon.Id, coupon.Code, coupon.Name, coupon.DiscountType, coupon.PercentOff, coupon.AmountOff,
        coupon.AmountOffCurrency, coupon.AppliesToTypes, coupon.AppliesToIds, coupon.Duration,
        coupon.DurationInMonths, coupon.MaxRedemptions, coupon.PerWorkspaceLimit, coupon.ValidFrom,
        coupon.ValidUntil, coupon.AutoApply, coupon.Status,
        new StripeSyncStatusDto(
            PackageCatalogRules.SyncState(coupon.StripeCouponId, coupon.StripeSyncError, coupon.StripeSyncedHash, PackageCatalogRules.Fingerprint(coupon)),
            coupon.StripeCouponId,
            new Dictionary<string, string>(),
            coupon.StripeSyncedAt,
            coupon.StripeSyncError),
        coupon.StripePromotionCodeId,
        sales?.UnitsSold ?? 0,
        Money(sales),
        coupon.CreatedAt, coupon.UpdatedAt, coupon.ArchivedAt);

    private static StripeSyncStatusDto StripeStatus(IStripeSyncedCatalogItem item, string fingerprint) => new(
        PackageCatalogRules.SyncState(item.StripeProductId, item.StripeSyncError, item.StripeSyncedHash, fingerprint),
        item.StripeProductId,
        PackageCatalogRules.ReadPriceIds(item.StripePriceIds),
        item.StripeSyncedAt,
        item.StripeSyncError);

    private static IReadOnlyList<MoneyDto> Money(CatalogItemSales? sales) =>
        sales?.Revenue.Select(r => new MoneyDto(r.Currency, r.Amount)).ToList() ?? [];

    private static Result<T> NotFound<T>() => Result.Failure<T>(PackageCatalogConstants.Errors.NotFound, ErrorCodes.NotFound);

    private static Result<T> Invalid<T>(string error) => Result.Failure<T>(error, ErrorCodes.ValidationError);

    private static Result<T> StripeNotConfigured<T>() =>
        Result.Failure<T>(PackageCatalogConstants.Errors.StripeNotConfigured, ErrorCodes.ServiceUnavailable);
}
