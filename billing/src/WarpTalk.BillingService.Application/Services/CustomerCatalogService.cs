using System.Globalization;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// G11 — what a workspace can buy from the catalog, and the server-side pricing of that purchase.
///
/// The catalog shown on the billing page and the checkout both go through <see cref="PackFor"/>
/// and <see cref="IsPlanEligible"/>, so a pack the page offers is exactly a pack checkout will sell,
/// and a request for anything else — an archived pack, another workspace's negotiated pack, a
/// currency the pack is not priced in — is refused with the reason.
///
/// CURRENCY: a workspace has no currency of its own; the web charges plans in the plan's currency
/// (WT-459/WT-518), so the catalog defaults to the workspace plan's currency, VND without one. A
/// checkout may name the other currency only when the item is priced in it.
/// </summary>
public sealed class CustomerCatalogService : ICustomerCatalogService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStripePaymentService _stripe;
    private readonly ILogger<CustomerCatalogService> _logger;
    private readonly TimeProvider _time;
    private readonly IEntitlementChangePublisher? _entitlements;

    public CustomerCatalogService(
        IUnitOfWork unitOfWork,
        IStripePaymentService stripe,
        ILogger<CustomerCatalogService> logger,
        IEntitlementChangePublisher? entitlements = null,
        TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _stripe = stripe;
        _logger = logger;
        _entitlements = entitlements;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private sealed record WorkspaceState(Guid WorkspaceId, Subscription? Subscription, Plan? Plan, bool HasActivePlan, string Currency);

    private async Task<WorkspaceState> LoadWorkspaceAsync(Guid workspaceId, CancellationToken ct)
    {
        // The same lookup the top-up grant uses: credits land on this row.
        var subscription = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null, ct);
        var plan = subscription is null ? null : await _unitOfWork.Plans.GetByIdAsync(subscription.PlanId, ct);
        var currency = PackageCatalogConstants.Currencies.Normalize(plan?.Currency) ?? PackageCatalogConstants.Currencies.Vnd;
        return new WorkspaceState(workspaceId, subscription, plan, subscription?.GrantsPlanEntitlements(Now) == true, currency);
    }

    // ---- Catalog ----------------------------------------------------------------------------

    public async Task<Result<WorkspaceCatalogDto>> GetCatalogAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<WorkspaceCatalogDto>(ApiMessageConstants.ValidationMessages.WorkspaceIdRequired, ErrorCodes.ValidationError);

        var state = await LoadWorkspaceAsync(workspaceId, ct);
        var campaigns = await _unitOfWork.Coupons.GetActiveAutoApplyAsync(ct);
        var campaignCounts = await CountsAsync(campaigns, workspaceId, ct);

        var packs = new List<CatalogCreditPackDto>();
        if (state.Subscription is not null)
        {
            foreach (var pack in await _unitOfWork.CreditPacks.FindAsync(p => p.Status == PackageCatalogConstants.Statuses.Active, ct))
            {
                var eligibility = await PackFor(pack, state, ct);
                if (eligibility.Error is not null) continue;

                var prices = PackageCatalogConstants.Currencies.All
                    .Select(currency => (currency, amount: pack.PriceFor(currency)))
                    .Where(p => p.amount is not null)
                    .Select(p => CatalogPrice(p.currency, null, p.amount!.Value, campaigns, campaignCounts,
                        PackageCatalogConstants.ItemTypes.CreditPack, pack.Id, recurring: false))
                    .ToList();

                packs.Add(new CatalogCreditPackDto(
                    pack.Id, pack.Slug, pack.Name, pack.Description, pack.Credits, pack.BonusCredits,
                    pack.ValidityDays, prices, eligibility.Remaining, pack.AvailableUntil));
            }
        }

        var open = await _unitOfWork.WorkspaceAddons.GetOpenForWorkspaceAsync(workspaceId, ct);
        var addons = new List<CatalogAddonDto>();
        if (state.HasActivePlan)
        {
            foreach (var addon in await _unitOfWork.Addons.FindAsync(a => a.Status == PackageCatalogConstants.Statuses.Active, ct))
            {
                if (!IsPlanEligible(addon, state)) continue;

                var prices = new List<CatalogPriceDto>();
                foreach (var cycle in new[] { SubscriptionConstants.BillingCycles.Monthly, SubscriptionConstants.BillingCycles.Yearly })
                {
                    foreach (var currency in PackageCatalogConstants.Currencies.All)
                    {
                        if (addon.PriceFor(cycle, currency) is { } amount)
                        {
                            prices.Add(CatalogPrice(currency, cycle, amount, campaigns, campaignCounts,
                                PackageCatalogConstants.ItemTypes.Addon, addon.Id, recurring: true));
                        }
                    }
                }

                addons.Add(new CatalogAddonDto(
                    addon.Id, addon.Slug, addon.Name, addon.Description, addon.UnitLabel, addon.EntitlementKey,
                    addon.UnitsPerQuantity, addon.MinQuantity, addon.MaxQuantity, prices,
                    open.Any(o => o.AddonId == addon.Id)));
            }
        }

        return Result.Success(new WorkspaceCatalogDto(
            workspaceId,
            state.Currency,
            state.Subscription is not null,
            state.HasActivePlan,
            state.Plan?.Slug,
            packs.OrderBy(p => p.Credits).ToList(),
            addons,
            open.Select(ToDto).ToList()));
    }

    private CatalogPriceDto CatalogPrice(
        string currency,
        string? cycle,
        decimal amount,
        IReadOnlyList<Coupon> campaigns,
        IReadOnlyDictionary<Guid, (int Total, int Workspace)> counts,
        string itemType,
        Guid itemId,
        bool recurring)
    {
        var best = PackageCatalogRules.BestAutoApply(campaigns, campaign => new PackageCatalogRules.CouponContext(
            itemType, itemId, currency, amount, recurring, Now,
            counts.GetValueOrDefault(campaign.Id).Total, counts.GetValueOrDefault(campaign.Id).Workspace));
        return new CatalogPriceDto(currency, cycle, amount, best?.Evaluation.Total, best?.Coupon.Name);
    }

    private async Task<IReadOnlyDictionary<Guid, (int Total, int Workspace)>> CountsAsync(
        IEnumerable<Coupon> coupons, Guid workspaceId, CancellationToken ct)
    {
        var counts = new Dictionary<Guid, (int, int)>();
        foreach (var coupon in coupons)
        {
            counts[coupon.Id] = (
                await _unitOfWork.CouponRedemptions.CountForCouponAsync(coupon.Id, ct),
                await _unitOfWork.CouponRedemptions.CountForWorkspaceAsync(coupon.Id, workspaceId, ct));
        }

        return counts;
    }

    private sealed record PackEligibility(string? Error, int? Remaining);

    /// <summary>Whether this workspace may buy <paramref name="pack"/> now, and how many more times.</summary>
    private async Task<PackEligibility> PackFor(CreditPack pack, WorkspaceState state, CancellationToken ct)
    {
        var now = Now;
        if (pack.Status != PackageCatalogConstants.Statuses.Active
            || (pack.AvailableFrom is { } from && now < from)
            || (pack.AvailableUntil is { } until && now >= until))
        {
            return new PackEligibility(PackageCatalogConstants.Errors.NotAvailable, null);
        }

        var visible = pack.Visibility switch
        {
            PackageCatalogConstants.Visibility.Public => true,
            PackageCatalogConstants.Visibility.Plans => state.Plan is not null && pack.EligiblePlanIds.Contains(state.Plan.Id),
            PackageCatalogConstants.Visibility.Workspaces => pack.EligibleWorkspaceIds.Contains(state.WorkspaceId),
            _ => false,
        };
        if (!visible) return new PackEligibility(PackageCatalogConstants.Errors.NotAvailable, null);

        if (pack.MaxTotal is { } maxTotal && await _unitOfWork.CreditPackPurchases.CountForPackAsync(pack.Id, ct) >= maxTotal)
            return new PackEligibility(PackageCatalogConstants.Errors.SoldOut, 0);

        int? remaining = null;
        if (pack.MaxPerWorkspace is { } perWorkspace)
        {
            remaining = Math.Max(0, perWorkspace - await _unitOfWork.CreditPackPurchases.CountForWorkspaceAsync(pack.Id, state.WorkspaceId, ct));
            if (remaining == 0) return new PackEligibility(PackageCatalogConstants.Errors.PurchaseLimitReached, 0);
        }

        return new PackEligibility(null, remaining);
    }

    private static bool IsPlanEligible(Addon addon, WorkspaceState state) =>
        state.HasActivePlan
        && state.Plan is not null
        && (addon.EligiblePlanIds.Length == 0 || addon.EligiblePlanIds.Contains(state.Plan.Id));

    // ---- Coupon preview ---------------------------------------------------------------------

    public async Task<Result<CouponPreviewDto>> PreviewCouponAsync(Guid workspaceId, CouponPreviewRequest request, CancellationToken ct = default)
    {
        var currency = PackageCatalogConstants.Currencies.Normalize(request.Currency);
        if (currency is null)
            return Result.Failure<CouponPreviewDto>("Currency must be VND or USD.", ErrorCodes.ValidationError);
        if (!PackageCatalogConstants.ItemTypes.All.Contains(request.ItemType, StringComparer.Ordinal))
            return Result.Failure<CouponPreviewDto>("Unknown item type.", ErrorCodes.ValidationError);

        var state = await LoadWorkspaceAsync(workspaceId, ct);
        decimal list;
        bool recurring;
        switch (request.ItemType)
        {
            case PackageCatalogConstants.ItemTypes.CreditPack:
                var pack = request.ItemId is { } packId ? await _unitOfWork.CreditPacks.GetByIdAsync(packId, ct) : null;
                if (pack is null || pack.PriceFor(currency) is not { } packPrice)
                    return Result.Failure<CouponPreviewDto>(PackageCatalogConstants.Errors.NotAvailable, ErrorCodes.ValidationError);
                list = packPrice;
                recurring = false;
                break;
            case PackageCatalogConstants.ItemTypes.Addon:
                var addon = request.ItemId is { } addonId ? await _unitOfWork.Addons.GetByIdAsync(addonId, ct) : null;
                var cycle = NormalizeCycle(request.BillingCycle);
                if (addon is null || addon.PriceFor(cycle, currency) is not { } addonPrice)
                    return Result.Failure<CouponPreviewDto>(PackageCatalogConstants.Errors.NotAvailable, ErrorCodes.ValidationError);
                list = addonPrice * Math.Clamp(request.Quantity <= 0 ? addon.MinQuantity : request.Quantity, addon.MinQuantity, addon.MaxQuantity);
                recurring = true;
                break;
            default:
                // A plan's price is the plans page's to show; the preview only needs it to compute
                // the amount off, and a wrong ListPrice only mis-states the preview — Stripe
                // applies the real coupon to the real price at checkout.
                list = Math.Max(0, request.ListPrice ?? 0);
                recurring = true;
                break;
        }

        var (coupon, evaluation, autoApplied) = await ResolveCouponAsync(
            request.Code, request.ItemType, request.ItemId, currency, list, recurring, state.WorkspaceId, ct);

        if (coupon is null || evaluation is null)
        {
            return Result.Success(new CouponPreviewDto(
                string.IsNullOrWhiteSpace(request.Code), string.IsNullOrWhiteSpace(request.Code) ? null : PackageCatalogConstants.Errors.CouponInvalid,
                null, null, null, false, currency, list, 0, list, null, null));
        }

        return Result.Success(new CouponPreviewDto(
            evaluation.Ok, evaluation.Error, coupon.Id, coupon.Code, coupon.Name, autoApplied, currency, list,
            evaluation.Ok ? evaluation.Discount : 0, evaluation.Ok ? evaluation.Total : list,
            coupon.Duration, coupon.DurationInMonths));
    }

    /// <summary>
    /// A typed code, or failing that the best running campaign. Returns the coupon even when it
    /// does not apply, so the caller can report why.
    /// </summary>
    private async Task<(Coupon? Coupon, PackageCatalogRules.CouponEvaluation? Evaluation, bool AutoApplied)> ResolveCouponAsync(
        string? code, string itemType, Guid? itemId, string currency, decimal list, bool recurring, Guid workspaceId, CancellationToken ct)
    {
        var normalized = PackageCatalogRules.NormalizeCode(code);
        if (normalized.Length > 0)
        {
            var coupon = await _unitOfWork.Coupons.GetByCodeAsync(normalized, ct);
            if (coupon is null) return (null, null, false);
            var evaluation = PackageCatalogRules.Evaluate(coupon, new PackageCatalogRules.CouponContext(
                itemType, itemId, currency, list, recurring, Now,
                await _unitOfWork.CouponRedemptions.CountForCouponAsync(coupon.Id, ct),
                await _unitOfWork.CouponRedemptions.CountForWorkspaceAsync(coupon.Id, workspaceId, ct)));
            return (coupon, evaluation, false);
        }

        var campaigns = await _unitOfWork.Coupons.GetActiveAutoApplyAsync(ct);
        var counts = await CountsAsync(campaigns, workspaceId, ct);
        var best = PackageCatalogRules.BestAutoApply(campaigns, campaign => new PackageCatalogRules.CouponContext(
            itemType, itemId, currency, list, recurring, Now,
            counts.GetValueOrDefault(campaign.Id).Total, counts.GetValueOrDefault(campaign.Id).Workspace));
        return best is null ? (null, null, true) : (best.Value.Coupon, best.Value.Evaluation, true);
    }

    // ---- Checkout ---------------------------------------------------------------------------

    public async Task<Result<(CreateCheckoutSessionRequest Request, CheckoutExtras Extras)>> PrepareCheckoutAsync(
        CreateCheckoutSessionRequest request,
        CancellationToken ct = default)
    {
        var type = request.PaymentType;
        if (string.Equals(type, PaymentConstants.PaymentTypes.CreditPack, StringComparison.OrdinalIgnoreCase))
            return await PreparePackAsync(request, ct);
        if (string.Equals(type, PaymentConstants.PaymentTypes.AddOn, StringComparison.OrdinalIgnoreCase))
            return await PrepareAddonAsync(request, ct);
        if (string.Equals(type, PaymentConstants.PaymentTypes.Subscription, StringComparison.OrdinalIgnoreCase))
            return await PreparePlanCouponAsync(request, ct);

        if (!string.IsNullOrWhiteSpace(request.CouponCode))
            return Fail(PackageCatalogConstants.Errors.CouponNotForTopUp);

        return Result.Success((request, CheckoutExtras.None));
    }

    private async Task<Result<(CreateCheckoutSessionRequest, CheckoutExtras)>> PreparePackAsync(CreateCheckoutSessionRequest request, CancellationToken ct)
    {
        var pack = request.PackageId is { } id ? await _unitOfWork.CreditPacks.GetByIdAsync(id, ct) : null;
        if (pack is null) return Fail(PackageCatalogConstants.Errors.NotFound, ErrorCodes.NotFound);

        var state = await LoadWorkspaceAsync(request.WorkspaceId, ct);
        if (state.Subscription is null) return Fail(PackageCatalogConstants.Errors.NoSubscription);

        var eligibility = await PackFor(pack, state, ct);
        if (eligibility.Error is not null) return Fail(eligibility.Error);

        var currency = PackageCatalogConstants.Currencies.Normalize(request.Currency) ?? state.Currency;
        if (pack.PriceFor(currency) is not { } price)
            return Fail(string.Format(PackageCatalogConstants.Errors.CurrencyNotOffered, currency.ToUpperInvariant()));

        var (coupon, evaluation, autoApplied) = await ResolveCouponAsync(
            request.CouponCode, PackageCatalogConstants.ItemTypes.CreditPack, pack.Id, currency, price, false, state.WorkspaceId, ct);
        if (!autoApplied && !string.IsNullOrWhiteSpace(request.CouponCode))
        {
            if (coupon is null) return Fail(PackageCatalogConstants.Errors.CouponInvalid);
            if (evaluation is { Ok: false }) return Fail(evaluation.Error!);
        }

        var applied = evaluation is { Ok: true } ? coupon : null;
        var priceIds = PackageCatalogRules.ReadPriceIds(pack.StripePriceIds);
        var packSynced = PackageCatalogRules.SyncState(pack.StripeProductId, pack.StripeSyncError, pack.StripeSyncedHash, PackageCatalogRules.Fingerprint(pack))
                         == PackageCatalogConstants.SyncStates.Synced;

        // Stripe discounts only when BOTH sides are in Stripe: the coupon, and a Product it can be
        // applied to. Otherwise the discount is taken off inline and the session carries the net
        // unit amount — the pack is one-off, so there is no later invoice to discount.
        var stripeDiscount = applied is not null && packSynced && !string.IsNullOrWhiteSpace(applied.StripeCouponId);
        var total = applied is null ? price : evaluation!.Total;
        var useStripePrice = packSynced && (applied is null || stripeDiscount) && priceIds.TryGetValue(PackageCatalogConstants.PriceKeys.Pack(currency), out _);

        var line = new CatalogCheckoutLine(
            pack.Name,
            packSynced ? pack.StripeProductId : null,
            useStripePrice ? priceIds[PackageCatalogConstants.PriceKeys.Pack(currency)] : null,
            stripeDiscount || applied is null ? price : total,
            currency,
            1,
            null);

        var metadata = new Dictionary<string, string>
        {
            [PackageCatalogConstants.StripeMetadata.PackageId] = pack.Id.ToString(),
            [PackageCatalogConstants.StripeMetadata.ListPrice] = price.ToString(CultureInfo.InvariantCulture),
        };
        if (applied is not null) metadata[PackageCatalogConstants.StripeMetadata.CouponId] = applied.Id.ToString();

        var priced = request with
        {
            Amount = total,
            Currency = currency,
            // The count the grant handler reads back off the session: base + bonus, fixed now so a
            // later edit of the pack cannot change what this payment buys.
            Credits = pack.TotalCredits,
            Quantity = 1,
        };

        return Result.Success((priced, new CheckoutExtras(
            line,
            stripeDiscount ? (UsePromotionCode(applied!, autoApplied) ? null : applied!.StripeCouponId) : null,
            stripeDiscount && UsePromotionCode(applied!, autoApplied) ? applied!.StripePromotionCodeId : null,
            metadata)));
    }

    private async Task<Result<(CreateCheckoutSessionRequest, CheckoutExtras)>> PrepareAddonAsync(CreateCheckoutSessionRequest request, CancellationToken ct)
    {
        var addon = request.PackageId is { } id ? await _unitOfWork.Addons.GetByIdAsync(id, ct) : null;
        if (addon is null || addon.Status != PackageCatalogConstants.Statuses.Active)
            return Fail(PackageCatalogConstants.Errors.NotAvailable);

        var state = await LoadWorkspaceAsync(request.WorkspaceId, ct);
        if (!state.HasActivePlan) return Fail(PackageCatalogConstants.Errors.AddonRequiresPlan);
        if (!IsPlanEligible(addon, state)) return Fail(PackageCatalogConstants.Errors.NotAvailable);

        var open = await _unitOfWork.WorkspaceAddons.GetOpenForWorkspaceAsync(state.WorkspaceId, ct);
        if (open.Any(o => o.AddonId == addon.Id)) return Fail(PackageCatalogConstants.Errors.AddonAlreadyActive);

        var quantity = request.Quantity <= 0 ? addon.MinQuantity : request.Quantity;
        if (quantity < addon.MinQuantity || quantity > addon.MaxQuantity)
            return Fail(string.Format(PackageCatalogConstants.Errors.AddonQuantityOutOfRange, addon.MinQuantity, addon.MaxQuantity));

        var cycle = NormalizeCycle(request.BillingCycle);
        var currency = PackageCatalogConstants.Currencies.Normalize(request.Currency) ?? state.Currency;
        if (addon.PriceFor(cycle, currency) is not { } unitPrice)
            return Fail(string.Format(PackageCatalogConstants.Errors.AddonCycleNotOffered, cycle, currency.ToUpperInvariant()));

        var list = unitPrice * quantity;
        var (coupon, evaluation, autoApplied) = await ResolveCouponAsync(
            request.CouponCode, PackageCatalogConstants.ItemTypes.Addon, addon.Id, currency, list, true, state.WorkspaceId, ct);
        if (!autoApplied && !string.IsNullOrWhiteSpace(request.CouponCode))
        {
            if (coupon is null) return Fail(PackageCatalogConstants.Errors.CouponInvalid);
            if (evaluation is { Ok: false }) return Fail(evaluation.Error!);
        }

        var applied = evaluation is { Ok: true } ? coupon : null;
        var priceIds = PackageCatalogRules.ReadPriceIds(addon.StripePriceIds);
        var addonSynced = PackageCatalogRules.SyncState(addon.StripeProductId, addon.StripeSyncError, addon.StripeSyncedHash, PackageCatalogRules.Fingerprint(addon))
                          == PackageCatalogConstants.SyncStates.Synced;

        // A coupon limited to specific products is limited in Stripe to their Stripe Products; on
        // an add-on Stripe does not know, Stripe would refuse it. A campaign then simply does not
        // apply; a typed code says why.
        if (applied is not null && applied.AppliesToIds.Length > 0 && !addonSynced)
        {
            if (!autoApplied) return Fail(PackageCatalogConstants.Errors.CouponRecurringNeedsStripe);
            applied = null;
        }
        var priceKey = PackageCatalogConstants.PriceKeys.Addon(cycle, currency);

        var line = new CatalogCheckoutLine(
            addon.Name,
            addonSynced ? addon.StripeProductId : null,
            addonSynced && priceIds.TryGetValue(priceKey, out var priceId) ? priceId : null,
            unitPrice,
            currency,
            quantity,
            BillingCycleResolver.ToPriceInterval(cycle) ?? PaymentConstants.PriceIntervals.Month);

        var metadata = new Dictionary<string, string>
        {
            [PackageCatalogConstants.StripeMetadata.PackageId] = addon.Id.ToString(),
            [PackageCatalogConstants.StripeMetadata.Quantity] = quantity.ToString(CultureInfo.InvariantCulture),
            [PackageCatalogConstants.StripeMetadata.ListPrice] = list.ToString(CultureInfo.InvariantCulture),
        };
        if (applied is not null) metadata[PackageCatalogConstants.StripeMetadata.CouponId] = applied.Id.ToString();

        var priced = request with
        {
            Amount = applied is null ? list : evaluation!.Total,
            Currency = currency,
            BillingCycle = cycle,
            Quantity = quantity,
            Credits = 0,
        };

        return Result.Success((priced, new CheckoutExtras(
            line,
            applied is not null && !UsePromotionCode(applied, autoApplied) ? applied.StripeCouponId : null,
            applied is not null && UsePromotionCode(applied, autoApplied) ? applied.StripePromotionCodeId : null,
            metadata)));
    }

    /// <summary>
    /// A plan checkout keeps its existing pricing; this only attaches a coupon (a typed code, or
    /// the best campaign for plans). Recurring, so the coupon must be in Stripe.
    /// </summary>
    private async Task<Result<(CreateCheckoutSessionRequest, CheckoutExtras)>> PreparePlanCouponAsync(CreateCheckoutSessionRequest request, CancellationToken ct)
    {
        var plan = string.IsNullOrWhiteSpace(request.PlanSlug)
            ? null
            : await _unitOfWork.Plans.FirstOrDefaultAsync(p => p.Slug.ToLower() == request.PlanSlug.ToLower() && p.DeletedAt == null, ct);
        if (plan is null)
        {
            return string.IsNullOrWhiteSpace(request.CouponCode)
                ? Result.Success((request, CheckoutExtras.None))
                : Fail(PackageCatalogConstants.Errors.CouponNotApplicable);
        }

        var currency = PackageCatalogConstants.Currencies.Normalize(request.Currency) ?? PackageCatalogConstants.Currencies.Vnd;
        var (coupon, evaluation, autoApplied) = await ResolveCouponAsync(
            request.CouponCode, PackageCatalogConstants.ItemTypes.Plan, plan.Id, currency, request.Amount, true, request.WorkspaceId, ct);

        if (!autoApplied && !string.IsNullOrWhiteSpace(request.CouponCode))
        {
            if (coupon is null) return Fail(PackageCatalogConstants.Errors.CouponInvalid);
            if (evaluation is { Ok: false }) return Fail(evaluation.Error!);
        }

        if (coupon is null || evaluation is not { Ok: true })
            return Result.Success((request, CheckoutExtras.None));

        var metadata = new Dictionary<string, string>
        {
            [PackageCatalogConstants.StripeMetadata.CouponId] = coupon.Id.ToString(),
            [PackageCatalogConstants.StripeMetadata.ListPrice] = request.Amount.ToString(CultureInfo.InvariantCulture),
        };

        return Result.Success((request, new CheckoutExtras(
            null,
            UsePromotionCode(coupon, autoApplied) ? null : coupon.StripeCouponId,
            UsePromotionCode(coupon, autoApplied) ? coupon.StripePromotionCodeId : null,
            metadata)));
    }

    /// <summary>
    /// A typed code goes to Stripe as its promotion code, so Stripe enforces the code's own
    /// redemption limit and expiry too; a campaign (or a code never given a promotion code) goes as
    /// the bare coupon.
    /// </summary>
    private static bool UsePromotionCode(Coupon coupon, bool autoApplied) =>
        !autoApplied && !string.IsNullOrWhiteSpace(coupon.StripePromotionCodeId);

    private static string NormalizeCycle(string? cycle) =>
        BillingCycleResolver.ToPriceInterval(cycle) == PaymentConstants.PriceIntervals.Year
            ? SubscriptionConstants.BillingCycles.Yearly
            : SubscriptionConstants.BillingCycles.Monthly;

    private static Result<(CreateCheckoutSessionRequest, CheckoutExtras)> Fail(string error, string code = ErrorCodes.ValidationError) =>
        Result.Failure<(CreateCheckoutSessionRequest, CheckoutExtras)>(error, code);

    // ---- Add-on management ------------------------------------------------------------------

    public async Task<Result<WorkspaceAddonDto>> CancelAddonAsync(Guid workspaceId, Guid workspaceAddonId, CancellationToken ct = default)
    {
        var row = await _unitOfWork.WorkspaceAddons.GetByIdAsync(workspaceAddonId, ct);
        if (row is null || row.WorkspaceId != workspaceId)
            return Result.Failure<WorkspaceAddonDto>(PackageCatalogConstants.Errors.NotFound, ErrorCodes.NotFound);
        if (row.Status != PackageCatalogConstants.WorkspaceAddonStatuses.Active)
            return Result.Failure<WorkspaceAddonDto>("This add-on is already cancelled.", ErrorCodes.InvalidState);

        if (!string.IsNullOrWhiteSpace(row.StripeSubscriptionId))
        {
            var cancelled = await _stripe.CancelStripeSubscriptionAtPeriodEndAsync(row.StripeSubscriptionId, ct);
            if (!cancelled.IsSuccess)
            {
                _logger.LogError("addon_cancel_failed: WorkspaceAddon={Id} StripeSubscription={Sub}: {Error}",
                    row.Id, row.StripeSubscriptionId, cancelled.Error);
                return Result.Failure<WorkspaceAddonDto>(cancelled.Error ?? "Stripe refused the cancellation.", ErrorCodes.BillingExternalServiceError);
            }

            row.CurrentPeriodEnd = cancelled.Value ?? row.CurrentPeriodEnd;
            row.Status = PackageCatalogConstants.WorkspaceAddonStatuses.Cancelling;
        }
        else
        {
            row.Status = PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled;
            row.CancelledAt = Now;
        }

        row.UpdatedAt = Now;
        _unitOfWork.WorkspaceAddons.Update(row);
        await _unitOfWork.SaveChangesAsync(ct);

        // Only an immediate cancellation changes what the workspace is entitled to now; a
        // cancelling add-on keeps granting until the period ends, when Stripe's
        // customer.subscription.deleted arrives and the payment path republishes.
        if (row.Status == PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled && _entitlements is not null)
        {
            await _entitlements.EnqueueAsync(workspaceId, EntitlementConstants.Reasons.AddonChanged, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        var addon = await _unitOfWork.Addons.GetByIdAsync(row.AddonId, ct);
        row.Addon = addon;
        return Result.Success(ToDto(row));
    }

    private static WorkspaceAddonDto ToDto(WorkspaceAddon row) => new(
        row.Id,
        row.AddonId,
        row.Addon?.Name ?? string.Empty,
        row.Addon?.UnitLabel ?? string.Empty,
        row.Addon?.EntitlementKey ?? string.Empty,
        row.Quantity,
        (row.Addon?.UnitsPerQuantity ?? 1) * row.Quantity,
        row.BillingCycle,
        row.Currency,
        row.UnitPrice,
        row.Status,
        row.CurrentPeriodEnd,
        row.StartedAt);
}
