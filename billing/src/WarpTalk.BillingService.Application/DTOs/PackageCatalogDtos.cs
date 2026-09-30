using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Application.DTOs;

// G11 — /admin/packages (credit packs, add-ons, coupons) and the customer catalog on the
// workspace billing page. Money is carried as decimal with a lower-case currency code; VND is
// whole dong, USD is dollars with two decimals.

/// <summary>Amount in one currency.</summary>
public sealed record MoneyDto(string Currency, decimal Amount);

/// <summary>How an item stands against Stripe, shown next to it in the admin.</summary>
public sealed record StripeSyncStatusDto(
    string State,
    string? ProductId,
    IReadOnlyDictionary<string, string> PriceIds,
    DateTime? SyncedAt,
    string? Error);

// ---- Credit packs --------------------------------------------------------------------------

public sealed record CreditPackRequest(
    string Slug,
    string Name,
    string? Description,
    int Credits,
    int BonusCredits,
    decimal? PriceVnd,
    decimal? PriceUsd,
    int? ValidityDays,
    string Visibility,
    IReadOnlyList<Guid>? EligiblePlanIds,
    IReadOnlyList<Guid>? EligibleWorkspaceIds,
    int? MaxPerWorkspace,
    int? MaxTotal,
    DateTime? AvailableFrom,
    DateTime? AvailableUntil,
    string Status,
    int SortOrder);

public sealed record CreditPackDto(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    int Credits,
    int BonusCredits,
    decimal? PriceVnd,
    decimal? PriceUsd,
    int? ValidityDays,
    string Visibility,
    IReadOnlyList<Guid> EligiblePlanIds,
    IReadOnlyList<Guid> EligibleWorkspaceIds,
    int? MaxPerWorkspace,
    int? MaxTotal,
    DateTime? AvailableFrom,
    DateTime? AvailableUntil,
    string Status,
    int SortOrder,
    StripeSyncStatusDto Stripe,
    int UnitsSold,
    IReadOnlyList<MoneyDto> Revenue,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ArchivedAt);

// ---- Add-ons -------------------------------------------------------------------------------

public sealed record AddonRequest(
    string Slug,
    string Name,
    string? Description,
    string UnitLabel,
    string EntitlementKey,
    int UnitsPerQuantity,
    decimal? PriceMonthlyVnd,
    decimal? PriceYearlyVnd,
    decimal? PriceMonthlyUsd,
    decimal? PriceYearlyUsd,
    int MinQuantity,
    int MaxQuantity,
    IReadOnlyList<Guid>? EligiblePlanIds,
    string Status,
    int SortOrder);

public sealed record AddonDto(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    string UnitLabel,
    string EntitlementKey,
    int UnitsPerQuantity,
    decimal? PriceMonthlyVnd,
    decimal? PriceYearlyVnd,
    decimal? PriceMonthlyUsd,
    decimal? PriceYearlyUsd,
    int MinQuantity,
    int MaxQuantity,
    IReadOnlyList<Guid> EligiblePlanIds,
    string Status,
    int SortOrder,
    StripeSyncStatusDto Stripe,
    int UnitsSold,
    int ActiveSubscribers,
    int ActiveQuantity,
    IReadOnlyList<MoneyDto> Revenue,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ArchivedAt);

// ---- Coupons -------------------------------------------------------------------------------

public sealed record CouponRequest(
    string? Code,
    string Name,
    string DiscountType,
    decimal? PercentOff,
    decimal? AmountOff,
    string? AmountOffCurrency,
    IReadOnlyList<string> AppliesToTypes,
    IReadOnlyList<Guid>? AppliesToIds,
    string Duration,
    int? DurationInMonths,
    int? MaxRedemptions,
    int PerWorkspaceLimit,
    DateTime? ValidFrom,
    DateTime? ValidUntil,
    bool AutoApply,
    string Status);

public sealed record CouponDto(
    Guid Id,
    string? Code,
    string Name,
    string DiscountType,
    decimal? PercentOff,
    decimal? AmountOff,
    string? AmountOffCurrency,
    IReadOnlyList<string> AppliesToTypes,
    IReadOnlyList<Guid> AppliesToIds,
    string Duration,
    int? DurationInMonths,
    int? MaxRedemptions,
    int PerWorkspaceLimit,
    DateTime? ValidFrom,
    DateTime? ValidUntil,
    bool AutoApply,
    string Status,
    StripeSyncStatusDto Stripe,
    string? StripePromotionCodeId,
    int Redemptions,
    IReadOnlyList<MoneyDto> DiscountGiven,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ArchivedAt);

// ---- Stripe sync / drift -------------------------------------------------------------------

/// <summary>One field on which Stripe and the database disagree.</summary>
public sealed record StripeDriftItemDto(string Field, string? Database, string? Stripe);

public sealed record StripeDriftDto(
    string ItemType,
    Guid ItemId,
    string SyncState,
    bool StripeReachable,
    DateTime CheckedAt,
    IReadOnlyList<StripeDriftItemDto> Differences,
    string? Error);

/// <summary>What the admin can pick from when building a package.</summary>
public sealed record PackageCatalogOptionsDto(
    IReadOnlyList<string> AddonEntitlementKeys,
    IReadOnlyList<string> NumericEntitlementKeys,
    IReadOnlyList<string> Currencies,
    IReadOnlyList<MoneyDto> MinimumCharges,
    bool StripeConfigured);

// ---- Customer catalog ----------------------------------------------------------------------

/// <summary>One price of a catalog item, with the auto-applied campaign when one is running.</summary>
public sealed record CatalogPriceDto(
    string Currency,
    string? BillingCycle,
    decimal Amount,
    decimal? DiscountedAmount,
    string? AutoCouponName);

public sealed record CatalogCreditPackDto(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    int Credits,
    int BonusCredits,
    int? ValidityDays,
    IReadOnlyList<CatalogPriceDto> Prices,
    int? PurchasesRemaining,
    DateTime? AvailableUntil);

public sealed record CatalogAddonDto(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    string UnitLabel,
    string EntitlementKey,
    int UnitsPerQuantity,
    int MinQuantity,
    int MaxQuantity,
    IReadOnlyList<CatalogPriceDto> Prices,
    bool Owned);

public sealed record WorkspaceAddonDto(
    Guid Id,
    Guid AddonId,
    string Name,
    string UnitLabel,
    string EntitlementKey,
    int Quantity,
    int UnitsGranted,
    string BillingCycle,
    string Currency,
    decimal UnitPrice,
    string Status,
    DateTime? CurrentPeriodEnd,
    DateTime StartedAt);

public sealed record WorkspaceCatalogDto(
    Guid WorkspaceId,
    string Currency,
    bool HasSubscription,
    bool HasActivePlan,
    string? PlanSlug,
    IReadOnlyList<CatalogCreditPackDto> CreditPacks,
    IReadOnlyList<CatalogAddonDto> Addons,
    IReadOnlyList<WorkspaceAddonDto> ActiveAddons);

public sealed record CouponPreviewRequest(
    string? Code,
    string ItemType,
    Guid? ItemId,
    string Currency,
    string? BillingCycle,
    int Quantity,
    decimal? ListPrice);

public sealed record CouponPreviewDto(
    bool Valid,
    string? Error,
    Guid? CouponId,
    string? Code,
    string? Name,
    bool AutoApplied,
    string Currency,
    decimal ListPrice,
    decimal Discount,
    decimal Total,
    string? Duration,
    int? DurationInMonths);

/// <summary>
/// Server-built line for a catalog checkout — never bound from a request body, so nothing on it
/// is a client input. <see cref="StripePriceId"/> is used when the item is synced; otherwise the
/// session carries inline price data for <see cref="UnitAmount"/>.
/// </summary>
public sealed record CatalogCheckoutLine(
    string ProductName,
    string? StripeProductId,
    string? StripePriceId,
    decimal UnitAmount,
    string Currency,
    int Quantity,
    string? RecurringInterval);

/// <summary>What a catalog checkout adds to the Stripe session besides the base request.</summary>
public sealed record CheckoutExtras(
    CatalogCheckoutLine? Line,
    string? StripeCouponId,
    string? StripePromotionCodeId,
    IReadOnlyDictionary<string, string> Metadata)
{
    public static CheckoutExtras None { get; } = new(null, null, null, new Dictionary<string, string>());
}
