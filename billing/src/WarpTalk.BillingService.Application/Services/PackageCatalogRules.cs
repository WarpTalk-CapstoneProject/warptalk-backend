using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// G11 — the pure rules of the sellable catalog: what a valid credit pack, add-on and coupon is,
/// how a coupon prices a purchase, and the fingerprint that tells "synced" from "edited since".
///
/// Pure on purpose: every rule here is what the server enforces, so it has to be testable without
/// a database, a clock or Stripe. The admin UI validates the same things for comfort; this is the
/// check that counts.
/// </summary>
public static partial class PackageCatalogRules
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{2,39}$")]
    private static partial Regex CodePattern();

    public static string NormalizeCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    // ---- Validation -------------------------------------------------------------------------

    public static string? ValidateCreditPack(CreditPackRequest request)
    {
        var common = ValidateCommon(request.Slug, request.Name, request.Description, request.Status);
        if (common is not null) return common;

        if (request.Credits <= 0 || request.Credits > PackageCatalogConstants.Limits.MaxCredits)
            return $"Credits must be between 1 and {PackageCatalogConstants.Limits.MaxCredits:N0}.";
        if (request.BonusCredits < 0 || request.BonusCredits > PackageCatalogConstants.Limits.MaxCredits)
            return "Bonus credits cannot be negative.";

        if (request.PriceVnd is null && request.PriceUsd is null)
            return "Set a price in at least one currency.";
        var price = ValidatePrice(request.PriceVnd, PackageCatalogConstants.Currencies.Vnd, "VND price")
                    ?? ValidatePrice(request.PriceUsd, PackageCatalogConstants.Currencies.Usd, "USD price");
        if (price is not null) return price;

        if (request.ValidityDays is { } days && (days <= 0 || days > PackageCatalogConstants.Limits.MaxValidityDays))
            return $"Validity must be between 1 and {PackageCatalogConstants.Limits.MaxValidityDays} days, or empty for credits that never expire.";

        if (!PackageCatalogConstants.Visibility.All.Contains(request.Visibility, StringComparer.Ordinal))
            return $"Visibility must be one of: {string.Join(", ", PackageCatalogConstants.Visibility.All)}.";
        if (request.Visibility == PackageCatalogConstants.Visibility.Plans && (request.EligiblePlanIds?.Count ?? 0) == 0)
            return "Choose at least one plan for a plan-restricted pack.";
        if (request.Visibility == PackageCatalogConstants.Visibility.Workspaces && (request.EligibleWorkspaceIds?.Count ?? 0) == 0)
            return "Choose at least one workspace for a workspace-restricted pack.";

        if (request.MaxPerWorkspace is <= 0) return "The per-workspace purchase limit must be at least 1, or empty for no limit.";
        if (request.MaxTotal is <= 0) return "The total purchase limit must be at least 1, or empty for no limit.";
        if (request.MaxPerWorkspace is { } perWs && request.MaxTotal is { } total && perWs > total)
            return "The per-workspace limit cannot exceed the total limit.";

        return ValidateWindow(request.AvailableFrom, request.AvailableUntil, "available");
    }

    public static string? ValidateAddon(AddonRequest request)
    {
        var common = ValidateCommon(request.Slug, request.Name, request.Description, request.Status);
        if (common is not null) return common;

        if (string.IsNullOrWhiteSpace(request.UnitLabel) || request.UnitLabel.Trim().Length > PackageCatalogConstants.Limits.UnitLabelMaxLength)
            return $"The unit label is required and at most {PackageCatalogConstants.Limits.UnitLabelMaxLength} characters.";

        if (!PackageCatalogConstants.AddonEntitlements.IsAllowed(request.EntitlementKey))
            return $"An add-on must raise one of the enforced entitlements: {string.Join(", ", PackageCatalogConstants.AddonEntitlements.All)}.";

        var numeric = EntitlementConstants.Keys.IsNumericLimit(request.EntitlementKey);
        if (request.UnitsPerQuantity <= 0 || request.UnitsPerQuantity > 10_000)
            return "Units per quantity must be between 1 and 10,000.";
        if (!numeric && request.UnitsPerQuantity != 1)
            return "A capability add-on switches the feature on; units per quantity must be 1.";

        if (request.MinQuantity < 1 || request.MaxQuantity < request.MinQuantity || request.MaxQuantity > PackageCatalogConstants.Limits.MaxAddonQuantity)
            return $"Quantities must satisfy 1 ≤ minimum ≤ maximum ≤ {PackageCatalogConstants.Limits.MaxAddonQuantity}.";
        if (!numeric && request.MaxQuantity != 1)
            return "A capability add-on is bought once; its maximum quantity must be 1.";

        if (request.PriceMonthlyVnd is null && request.PriceYearlyVnd is null
            && request.PriceMonthlyUsd is null && request.PriceYearlyUsd is null)
            return "Set at least one price.";

        return ValidatePrice(request.PriceMonthlyVnd, PackageCatalogConstants.Currencies.Vnd, "Monthly VND price")
               ?? ValidatePrice(request.PriceYearlyVnd, PackageCatalogConstants.Currencies.Vnd, "Yearly VND price")
               ?? ValidatePrice(request.PriceMonthlyUsd, PackageCatalogConstants.Currencies.Usd, "Monthly USD price")
               ?? ValidatePrice(request.PriceYearlyUsd, PackageCatalogConstants.Currencies.Usd, "Yearly USD price");
    }

    public static string? ValidateCoupon(CouponRequest request)
    {
        var code = NormalizeCode(request.Code);
        if (code.Length > 0 && !CodePattern().IsMatch(code))
            return "A code is 3–40 characters: letters, digits, '-' and '_'.";
        if (code.Length == 0 && !request.AutoApply)
            return "A coupon needs a code, or must be an auto-apply campaign.";

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > PackageCatalogConstants.Limits.NameMaxLength)
            return $"The name is required and at most {PackageCatalogConstants.Limits.NameMaxLength} characters.";
        if (!IsEditableStatus(request.Status))
            return "Status must be draft or active. Archive a coupon with the archive action.";

        switch (request.DiscountType)
        {
            case PackageCatalogConstants.DiscountTypes.Percent:
                if (request.PercentOff is not { } percent || percent <= 0 || percent > 100)
                    return "A percentage discount must be more than 0 and at most 100.";
                if (decimal.Round(percent, 2) != percent)
                    return "A percentage has at most two decimals.";
                if (request.AmountOff is not null)
                    return "A percentage coupon has no fixed amount.";
                break;
            case PackageCatalogConstants.DiscountTypes.Fixed:
                var currency = PackageCatalogConstants.Currencies.Normalize(request.AmountOffCurrency);
                if (currency is null)
                    return "A fixed discount needs a currency (VND or USD).";
                if (request.AmountOff is not { } amount || amount <= 0)
                    return "A fixed discount must be more than 0.";
                if (!HasValidScale(amount, currency))
                    return currency == PackageCatalogConstants.Currencies.Vnd
                        ? "A VND amount is whole dong."
                        : "A USD amount has at most two decimals.";
                if (request.PercentOff is not null)
                    return "A fixed coupon has no percentage.";
                break;
            default:
                return "The discount type must be percent or fixed.";
        }

        if (request.AppliesToTypes is not { Count: > 0 }
            || request.AppliesToTypes.Any(type => !PackageCatalogConstants.ItemTypes.All.Contains(type, StringComparer.Ordinal)))
            return "Choose what the coupon applies to: plans, credit packs and/or add-ons.";

        switch (request.Duration)
        {
            case PackageCatalogConstants.Durations.Repeating:
                if (request.DurationInMonths is not { } months || months < 1 || months > 36)
                    return "A repeating coupon lasts 1 to 36 months.";
                break;
            case PackageCatalogConstants.Durations.Once:
            case PackageCatalogConstants.Durations.Forever:
                if (request.DurationInMonths is not null)
                    return "Only a repeating coupon has a number of months.";
                break;
            default:
                return "Duration must be once, repeating or forever.";
        }

        if (request.MaxRedemptions is <= 0) return "The redemption limit must be at least 1, or empty for no limit.";
        if (request.PerWorkspaceLimit < 1) return "Each workspace must be allowed at least one redemption.";
        if (request.MaxRedemptions is { } max && request.PerWorkspaceLimit > max)
            return "The per-workspace limit cannot exceed the total redemption limit.";

        return ValidateWindow(request.ValidFrom, request.ValidUntil, "valid");
    }

    /// <summary>
    /// A price in one currency: optional, positive, in the currency's own precision, at least what
    /// Stripe can charge and below an absurd ceiling that catches a mistyped extra zero.
    /// </summary>
    public static string? ValidatePrice(decimal? price, string currency, string label)
    {
        if (price is not { } value) return null;
        if (value <= 0) return $"{label} must be more than 0.";
        if (!HasValidScale(value, currency))
            return currency == PackageCatalogConstants.Currencies.Vnd
                ? $"{label} is whole dong (VND has no decimals)."
                : $"{label} has at most two decimals.";

        var minimum = PackageCatalogConstants.Currencies.MinimumCharge(currency);
        if (value < minimum)
            return $"{label} is below the smallest amount Stripe can charge ({minimum.ToString(CultureInfo.InvariantCulture)} {currency.ToUpperInvariant()}).";

        var ceiling = currency == PackageCatalogConstants.Currencies.Vnd
            ? PackageCatalogConstants.Limits.MaxPriceVnd
            : PackageCatalogConstants.Limits.MaxPriceUsd;
        return value > ceiling ? $"{label} is above the maximum allowed." : null;
    }

    private static bool HasValidScale(decimal value, string currency) =>
        PackageCatalogConstants.Currencies.IsZeroDecimal(currency)
            ? decimal.Truncate(value) == value
            : decimal.Round(value, 2) == value;

    private static string? ValidateCommon(string slug, string name, string? description, string status)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > PackageCatalogConstants.Limits.SlugMaxLength || !SlugPattern().IsMatch(slug))
            return "The slug is lower-case letters, digits and single hyphens, at most 60 characters.";
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > PackageCatalogConstants.Limits.NameMaxLength)
            return $"The name is required and at most {PackageCatalogConstants.Limits.NameMaxLength} characters.";
        if (description is { Length: > PackageCatalogConstants.Limits.DescriptionMaxLength })
            return $"The description is at most {PackageCatalogConstants.Limits.DescriptionMaxLength} characters.";
        return IsEditableStatus(status) ? null : "Status must be draft or active. Archive an item with the archive action.";
    }

    private static bool IsEditableStatus(string status) =>
        status is PackageCatalogConstants.Statuses.Draft or PackageCatalogConstants.Statuses.Active;

    private static string? ValidateWindow(DateTime? from, DateTime? until, string label) =>
        from is { } start && until is { } end && start >= end
            ? $"The {label}-from date must be before the {label}-until date."
            : null;

    // ---- Coupon pricing ---------------------------------------------------------------------

    /// <summary>
    /// The facts a coupon is judged against, gathered by the caller (limits need two counts from
    /// the database; everything else is on the purchase).
    /// </summary>
    public sealed record CouponContext(
        string ItemType,
        Guid? ItemId,
        string Currency,
        decimal ListTotal,
        bool Recurring,
        DateTime NowUtc,
        int RedemptionsTotal,
        int RedemptionsByWorkspace);

    public sealed record CouponEvaluation(bool Ok, string? Error, decimal Discount, decimal Total)
    {
        public static CouponEvaluation Fail(string error, decimal listTotal) => new(false, error, 0, listTotal);
    }

    /// <summary>
    /// Whether <paramref name="coupon"/> applies, and what it takes off.
    ///
    /// THE STACKING RULE: one coupon per checkout, full stop — Stripe Checkout itself accepts a
    /// single discount per session. A typed code replaces any auto-apply campaign rather than
    /// adding to it, and the best campaign wins when several apply. Coupons never touch a custom
    /// credit top-up or an invoice payment, whose amounts are not catalog prices.
    /// </summary>
    public static CouponEvaluation Evaluate(Coupon coupon, CouponContext context)
    {
        if (coupon.Status != PackageCatalogConstants.Statuses.Active)
            return CouponEvaluation.Fail(PackageCatalogConstants.Errors.CouponInvalid, context.ListTotal);
        if (coupon.ValidFrom is { } from && context.NowUtc < from)
            return CouponEvaluation.Fail(PackageCatalogConstants.Errors.CouponInvalid, context.ListTotal);
        if (coupon.ValidUntil is { } until && context.NowUtc >= until)
            return CouponEvaluation.Fail(PackageCatalogConstants.Errors.CouponInvalid, context.ListTotal);

        if (!coupon.AppliesToTypes.Contains(context.ItemType, StringComparer.Ordinal))
            return CouponEvaluation.Fail(PackageCatalogConstants.Errors.CouponNotApplicable, context.ListTotal);
        if (coupon.AppliesToIds.Length > 0 && (context.ItemId is null || !coupon.AppliesToIds.Contains(context.ItemId.Value)))
            return CouponEvaluation.Fail(PackageCatalogConstants.Errors.CouponNotApplicable, context.ListTotal);

        if (coupon.MaxRedemptions is { } max && context.RedemptionsTotal >= max)
            return CouponEvaluation.Fail(PackageCatalogConstants.Errors.CouponExhausted, context.ListTotal);
        if (context.RedemptionsByWorkspace >= coupon.PerWorkspaceLimit)
            return CouponEvaluation.Fail(PackageCatalogConstants.Errors.CouponWorkspaceLimit, context.ListTotal);

        var currency = PackageCatalogConstants.Currencies.Normalize(context.Currency) ?? context.Currency;
        if (coupon.DiscountType == PackageCatalogConstants.DiscountTypes.Fixed
            && !string.Equals(coupon.AmountOffCurrency, currency, StringComparison.OrdinalIgnoreCase))
        {
            return CouponEvaluation.Fail(
                string.Format(PackageCatalogConstants.Errors.CouponCurrencyMismatch,
                    (coupon.AmountOffCurrency ?? "?").ToUpperInvariant(), currency.ToUpperInvariant()),
                context.ListTotal);
        }

        // A recurring purchase is discounted by Stripe on every invoice the coupon covers; only a
        // coupon Stripe knows about can do that. A one-off purchase can be discounted inline.
        if (context.Recurring && string.IsNullOrWhiteSpace(coupon.StripeCouponId))
            return CouponEvaluation.Fail(PackageCatalogConstants.Errors.CouponRecurringNeedsStripe, context.ListTotal);

        var discount = Discount(coupon, context.ListTotal, currency);
        var total = context.ListTotal - discount;

        // A one-off total has to be chargeable. A subscription can legitimately start at zero
        // under a 100% coupon, which Stripe supports.
        if (!context.Recurring && total < PackageCatalogConstants.Currencies.MinimumCharge(currency))
            return CouponEvaluation.Fail(PackageCatalogConstants.Errors.CouponBelowMinimum, context.ListTotal);

        return new CouponEvaluation(true, null, discount, total);
    }

    /// <summary>
    /// What <paramref name="coupon"/> takes off <paramref name="listTotal"/>, rounded to the
    /// currency's precision the way Stripe rounds (half away from zero), never more than the total.
    /// </summary>
    public static decimal Discount(Coupon coupon, decimal listTotal, string currency)
    {
        var decimals = PackageCatalogConstants.Currencies.IsZeroDecimal(currency) ? 0 : 2;
        var raw = coupon.DiscountType == PackageCatalogConstants.DiscountTypes.Percent
            ? listTotal * (coupon.PercentOff ?? 0) / 100m
            : coupon.AmountOff ?? 0;
        var rounded = decimal.Round(raw, decimals, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 0, listTotal);
    }

    /// <summary>The best applicable auto-apply campaign, or null.</summary>
    public static (Coupon Coupon, CouponEvaluation Evaluation)? BestAutoApply(
        IEnumerable<Coupon> campaigns,
        Func<Coupon, CouponContext> contextFor)
    {
        (Coupon, CouponEvaluation)? best = null;
        foreach (var campaign in campaigns.Where(c => c.AutoApply))
        {
            var evaluation = Evaluate(campaign, contextFor(campaign));
            if (!evaluation.Ok) continue;
            if (best is null || evaluation.Discount > best.Value.Item2.Discount)
                best = (campaign, evaluation);
        }

        return best;
    }

    // ---- Stripe sync state ------------------------------------------------------------------

    public static string SyncState(string? stripeObjectId, string? syncError, string? syncedHash, string currentHash)
    {
        if (!string.IsNullOrWhiteSpace(syncError)) return PackageCatalogConstants.SyncStates.Error;
        if (string.IsNullOrWhiteSpace(stripeObjectId)) return PackageCatalogConstants.SyncStates.NotSynced;
        return string.Equals(syncedHash, currentHash, StringComparison.Ordinal)
            ? PackageCatalogConstants.SyncStates.Synced
            : PackageCatalogConstants.SyncStates.Outdated;
    }

    /// <summary>The terms Stripe holds for a pack. Sort order, limits and visibility are ours alone.</summary>
    public static string Fingerprint(CreditPack pack) => Hash(
        "pack", pack.Name, pack.Description, Money(pack.PriceVnd), Money(pack.PriceUsd), IsLive(pack.Status));

    public static string Fingerprint(Addon addon) => Hash(
        "addon", addon.Name, addon.Description, addon.UnitLabel,
        Money(addon.PriceMonthlyVnd), Money(addon.PriceYearlyVnd),
        Money(addon.PriceMonthlyUsd), Money(addon.PriceYearlyUsd), IsLive(addon.Status));

    public static string Fingerprint(Coupon coupon) => Hash(
        "coupon", coupon.Name, coupon.Code, coupon.DiscountType, Money(coupon.PercentOff), Money(coupon.AmountOff),
        coupon.AmountOffCurrency, coupon.Duration, coupon.DurationInMonths?.ToString(CultureInfo.InvariantCulture),
        coupon.MaxRedemptions?.ToString(CultureInfo.InvariantCulture),
        coupon.ValidUntil?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        string.Join(',', coupon.AppliesToTypes.OrderBy(t => t, StringComparer.Ordinal)),
        string.Join(',', coupon.AppliesToIds.OrderBy(id => id)),
        IsLive(coupon.Status));

    private static string IsLive(string status) => status == PackageCatalogConstants.Statuses.Archived ? "archived" : "live";

    private static string? Money(decimal? value) => value?.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Hash(params string?[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(p => p ?? "␀"))));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ---- stripe_price_ids jsonb -------------------------------------------------------------

    public static IReadOnlyDictionary<string, string> ReadPriceIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string WritePriceIds(IReadOnlyDictionary<string, string> ids) =>
        JsonSerializer.Serialize(ids.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>
    /// A slug no other row uses, for a duplicate: <c>starter-pack-copy</c>, then <c>-copy-2</c>, …
    /// </summary>
    public static string CopySlug(string slug, Func<string, bool> taken)
    {
        var stem = slug.Length > PackageCatalogConstants.Limits.SlugMaxLength - 8
            ? slug[..(PackageCatalogConstants.Limits.SlugMaxLength - 8)].TrimEnd('-')
            : slug;
        var candidate = $"{stem}-copy";
        for (var n = 2; taken(candidate); n++)
        {
            candidate = $"{stem}-copy-{n}";
        }

        return candidate;
    }
}
