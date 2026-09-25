using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Stripe;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Services;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>
/// G11 — pushes the admin catalog to Stripe and reads it back.
///
/// THE RULES STRIPE IMPOSES, and how each is honoured:
///   • A Price's amount, currency and interval are immutable. A changed price therefore creates a
///     NEW Price and deactivates the old one; <c>stripe_price_ids</c> always names the current one.
///     Checkouts already open on the old Price still complete — a deactivated Price can be paid,
///     it just cannot start new sessions.
///   • A Coupon's discount terms are immutable (only its name can change). Changed terms create a
///     NEW Stripe coupon; the old one is left alone — subscriptions already discounted by it keep
///     their discount, as promised — and its promotion code is deactivated.
///   • Nothing is ever deleted. Archiving deactivates the Product, its Prices and the promotion code.
///
/// The Stripe account on production is a Sandbox (cs_test_ sessions are expected there). The API
/// key never appears in anything this class returns: Stripe's own error text is passed through
/// <see cref="Redact"/> before it is stored or shown.
/// </summary>
public sealed partial class StripeCatalogSyncService : IStripeCatalogSync
{
    private const int MaxErrorLength = 500;

    private readonly IStripeSdkClient _sdk;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;

    public StripeCatalogSyncService(IStripeSdkClient sdk, IConfiguration configuration, TimeProvider? time = null)
    {
        _sdk = sdk;
        _configuration = configuration;
        _time = time ?? TimeProvider.System;
    }

    public bool IsConfigured
    {
        get
        {
            var key = _configuration[PaymentConstants.StripeConfigKeys.SecretKey];
            return !string.IsNullOrWhiteSpace(key) && key != PaymentConstants.StripePlaceholders.SecretKeyPlaceholder;
        }
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ---- Sync -------------------------------------------------------------------------------

    public async Task<string?> SyncCreditPackAsync(Domain.Entities.CreditPack pack, CancellationToken ct = default)
    {
        try
        {
            var live = pack.Status != PackageCatalogConstants.Statuses.Archived;
            var metadata = Metadata(PackageCatalogConstants.ItemTypes.CreditPack, pack.Id, pack.Slug);
            pack.StripeProductId = await EnsureProductAsync(pack.StripeProductId, pack.Name, pack.Description, live, metadata, ct);

            var ids = new Dictionary<string, string>(PackageCatalogRules.ReadPriceIds(pack.StripePriceIds));
            foreach (var currency in PackageCatalogConstants.Currencies.All)
            {
                await EnsurePriceAsync(ids, PackageCatalogConstants.PriceKeys.Pack(currency), pack.StripeProductId,
                    pack.PriceFor(currency), currency, null, live, metadata, ct);
            }

            pack.StripePriceIds = PackageCatalogRules.WritePriceIds(ids);
            pack.StripeSyncedAt = Now;
            pack.StripeSyncError = null;
            pack.StripeSyncedHash = PackageCatalogRules.Fingerprint(pack);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return pack.StripeSyncError = Redact(ex);
        }
    }

    public async Task<string?> SyncAddonAsync(Domain.Entities.Addon addon, CancellationToken ct = default)
    {
        try
        {
            var live = addon.Status != PackageCatalogConstants.Statuses.Archived;
            var metadata = Metadata(PackageCatalogConstants.ItemTypes.Addon, addon.Id, addon.Slug);
            metadata["entitlement_key"] = addon.EntitlementKey;
            addon.StripeProductId = await EnsureProductAsync(addon.StripeProductId, addon.Name, addon.Description, live, metadata, ct);

            var ids = new Dictionary<string, string>(PackageCatalogRules.ReadPriceIds(addon.StripePriceIds));
            foreach (var cycle in new[] { SubscriptionConstants.BillingCycles.Monthly, SubscriptionConstants.BillingCycles.Yearly })
            {
                foreach (var currency in PackageCatalogConstants.Currencies.All)
                {
                    await EnsurePriceAsync(ids, PackageCatalogConstants.PriceKeys.Addon(cycle, currency), addon.StripeProductId,
                        addon.PriceFor(cycle, currency), currency, BillingCycleResolver.ToPriceInterval(cycle), live, metadata, ct);
                }
            }

            addon.StripePriceIds = PackageCatalogRules.WritePriceIds(ids);
            addon.StripeSyncedAt = Now;
            addon.StripeSyncError = null;
            addon.StripeSyncedHash = PackageCatalogRules.Fingerprint(addon);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return addon.StripeSyncError = Redact(ex);
        }
    }

    public async Task<string?> SyncCouponAsync(Domain.Entities.Coupon coupon, IReadOnlyCollection<string> productIds, CancellationToken ct = default)
    {
        try
        {
            var live = coupon.Status != PackageCatalogConstants.Statuses.Archived;
            var couponChanged = false;

            if (live)
            {
                if (coupon.AppliesToIds.Length > 0 && productIds.Count == 0
                    && !coupon.AppliesToTypes.Contains(PackageCatalogConstants.ItemTypes.Plan))
                {
                    // Limited to specific packs/add-ons none of which Stripe knows yet: Stripe
                    // would accept the coupon unrestricted, i.e. wider than it is here.
                    throw new InvalidOperationException("Sync the credit packs / add-ons this coupon is limited to first.");
                }

                if (coupon.ValidUntil is { } until && until <= Now)
                {
                    throw new InvalidOperationException("This coupon's validity has ended; Stripe will not create a coupon that can no longer be redeemed.");
                }

                Coupon? existing = null;
                if (!string.IsNullOrWhiteSpace(coupon.StripeCouponId))
                {
                    existing = await _sdk.GetCouponAsync(coupon.StripeCouponId, ct);
                }

                if (existing is null || existing.Deleted == true || CouponTermDifferences(coupon, existing, productIds).Count > 0)
                {
                    var created = await _sdk.CreateCouponAsync(CouponOptions(coupon, productIds), ct);
                    coupon.StripeCouponId = created.Id;
                    couponChanged = true;
                }
                else if (!string.Equals(existing.Name, StripeName(coupon.Name), StringComparison.Ordinal))
                {
                    await _sdk.UpdateCouponAsync(existing.Id, new CouponUpdateOptions { Name = StripeName(coupon.Name) }, ct);
                }
            }

            await SyncPromotionCodeAsync(coupon, live, couponChanged, ct);

            coupon.StripeSyncedAt = Now;
            coupon.StripeSyncError = null;
            coupon.StripeSyncedHash = PackageCatalogRules.Fingerprint(coupon);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return coupon.StripeSyncError = Redact(ex);
        }
    }

    /// <summary>
    /// A typed code is a Stripe promotion code on the coupon, so Stripe enforces its expiry and
    /// redemption cap too. Promotion codes cannot be edited or deleted: a changed code or a new
    /// underlying coupon deactivates the old one and creates another.
    /// </summary>
    private async Task SyncPromotionCodeAsync(Domain.Entities.Coupon coupon, bool live, bool couponChanged, CancellationToken ct)
    {
        PromotionCode? current = null;
        if (!string.IsNullOrWhiteSpace(coupon.StripePromotionCodeId))
        {
            current = await _sdk.GetPromotionCodeAsync(coupon.StripePromotionCodeId, ct);
        }

        var wanted = live && !string.IsNullOrWhiteSpace(coupon.Code) && !string.IsNullOrWhiteSpace(coupon.StripeCouponId);
        var reusable = current is not null && !couponChanged
                       && string.Equals(current.Code, coupon.Code, StringComparison.OrdinalIgnoreCase)
                       && current.MaxRedemptions == coupon.MaxRedemptions
                       && Same(current.ExpiresAt, coupon.ValidUntil);

        if (current is not null && current.Active && (!wanted || !reusable))
        {
            await _sdk.UpdatePromotionCodeAsync(current.Id, new PromotionCodeUpdateOptions { Active = false }, ct);
        }

        if (!wanted)
        {
            if (string.IsNullOrWhiteSpace(coupon.Code)) coupon.StripePromotionCodeId = null;
            return;
        }

        if (reusable)
        {
            if (!current!.Active)
            {
                await _sdk.UpdatePromotionCodeAsync(current.Id, new PromotionCodeUpdateOptions { Active = true }, ct);
            }

            return;
        }

        var promotion = await _sdk.CreatePromotionCodeAsync(new PromotionCodeCreateOptions
        {
            Code = coupon.Code,
            Promotion = new PromotionCodePromotionOptions { Type = "coupon", Coupon = coupon.StripeCouponId },
            MaxRedemptions = coupon.MaxRedemptions,
            ExpiresAt = coupon.ValidUntil,
            Active = true,
            Metadata = Metadata("coupon", coupon.Id, coupon.Code!),
        }, ct);
        coupon.StripePromotionCodeId = promotion.Id;
    }

    private async Task<string> EnsureProductAsync(
        string? productId, string name, string? description, bool live, Dictionary<string, string> metadata, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(productId))
        {
            if (!live)
            {
                throw new InvalidOperationException("An archived item is not created in Stripe.");
            }

            var product = await _sdk.CreateProductAsync(new ProductCreateOptions
            {
                Name = StripeName(name),
                Description = string.IsNullOrWhiteSpace(description) ? null : description,
                Active = true,
                Metadata = metadata,
            }, ct);
            return product.Id;
        }

        var update = new ProductUpdateOptions { Name = StripeName(name), Active = live, Metadata = metadata };
        if (!string.IsNullOrWhiteSpace(description))
        {
            update.Description = description;
        }

        await _sdk.UpdateProductAsync(productId, update, ct);
        return productId;
    }

    private async Task EnsurePriceAsync(
        Dictionary<string, string> ids,
        string key,
        string productId,
        decimal? amount,
        string currency,
        string? interval,
        bool live,
        Dictionary<string, string> metadata,
        CancellationToken ct)
    {
        ids.TryGetValue(key, out var existingId);

        if (!string.IsNullOrWhiteSpace(existingId))
        {
            var existing = await _sdk.GetPriceAsync(existingId, ct);
            if (amount is { } wanted && PriceMatches(existing, productId, wanted, currency, interval))
            {
                if (existing.Active != live)
                {
                    await _sdk.UpdatePriceAsync(existing.Id, new PriceUpdateOptions { Active = live }, ct);
                }

                return;
            }

            // Changed or withdrawn: the old Price is archived, never deleted.
            if (existing.Active)
            {
                await _sdk.UpdatePriceAsync(existing.Id, new PriceUpdateOptions { Active = false }, ct);
            }

            ids.Remove(key);
        }

        if (amount is not { } unitAmount || !live)
        {
            return;
        }

        var price = await _sdk.CreatePriceAsync(new PriceCreateOptions
        {
            Product = productId,
            Currency = currency,
            UnitAmount = StripePaymentService.ToMinorUnits(unitAmount, currency),
            Recurring = interval is null ? null : new PriceRecurringOptions { Interval = interval },
            Nickname = key,
            Metadata = metadata,
        }, ct);
        ids[key] = price.Id;
    }

    private static bool PriceMatches(Price price, string productId, decimal amount, string currency, string? interval) =>
        string.Equals(price.ProductId, productId, StringComparison.Ordinal)
        && string.Equals(price.Currency, currency, StringComparison.OrdinalIgnoreCase)
        && price.UnitAmount == StripePaymentService.ToMinorUnits(amount, currency)
        && string.Equals(price.Recurring?.Interval, interval, StringComparison.Ordinal);

    private static CouponCreateOptions CouponOptions(Domain.Entities.Coupon coupon, IReadOnlyCollection<string> productIds)
    {
        var options = new CouponCreateOptions
        {
            Name = StripeName(coupon.Name),
            Duration = coupon.Duration,
            DurationInMonths = coupon.Duration == PackageCatalogConstants.Durations.Repeating ? coupon.DurationInMonths : null,
            MaxRedemptions = coupon.MaxRedemptions,
            RedeemBy = coupon.ValidUntil,
            Metadata = Metadata("coupon", coupon.Id, coupon.Code ?? coupon.Name),
        };

        if (coupon.DiscountType == PackageCatalogConstants.DiscountTypes.Percent)
        {
            options.PercentOff = coupon.PercentOff;
        }
        else
        {
            options.AmountOff = StripePaymentService.ToMinorUnits(coupon.AmountOff ?? 0, coupon.AmountOffCurrency ?? PackageCatalogConstants.Currencies.Vnd);
            options.Currency = coupon.AmountOffCurrency;
        }

        if (productIds.Count > 0 && !coupon.AppliesToTypes.Contains(PackageCatalogConstants.ItemTypes.Plan))
        {
            options.AppliesTo = new CouponAppliesToOptions { Products = productIds.Distinct().ToList() };
        }

        return options;
    }

    // ---- Drift ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<StripeDriftItemDto>> CompareCreditPackAsync(Domain.Entities.CreditPack pack, CancellationToken ct = default)
    {
        var differences = new List<StripeDriftItemDto>();
        var live = pack.Status != PackageCatalogConstants.Statuses.Archived;
        await CompareProductAsync(pack.StripeProductId!, pack.Name, pack.Description, live, differences, ct);

        var ids = PackageCatalogRules.ReadPriceIds(pack.StripePriceIds);
        foreach (var currency in PackageCatalogConstants.Currencies.All)
        {
            await ComparePriceAsync(ids, PackageCatalogConstants.PriceKeys.Pack(currency), pack.PriceFor(currency),
                currency, null, live, differences, ct);
        }

        return differences;
    }

    public async Task<IReadOnlyList<StripeDriftItemDto>> CompareAddonAsync(Domain.Entities.Addon addon, CancellationToken ct = default)
    {
        var differences = new List<StripeDriftItemDto>();
        var live = addon.Status != PackageCatalogConstants.Statuses.Archived;
        await CompareProductAsync(addon.StripeProductId!, addon.Name, addon.Description, live, differences, ct);

        var ids = PackageCatalogRules.ReadPriceIds(addon.StripePriceIds);
        foreach (var cycle in new[] { SubscriptionConstants.BillingCycles.Monthly, SubscriptionConstants.BillingCycles.Yearly })
        {
            foreach (var currency in PackageCatalogConstants.Currencies.All)
            {
                await ComparePriceAsync(ids, PackageCatalogConstants.PriceKeys.Addon(cycle, currency), addon.PriceFor(cycle, currency),
                    currency, BillingCycleResolver.ToPriceInterval(cycle), live, differences, ct);
            }
        }

        return differences;
    }

    public async Task<IReadOnlyList<StripeDriftItemDto>> CompareCouponAsync(Domain.Entities.Coupon coupon, CancellationToken ct = default)
    {
        var differences = new List<StripeDriftItemDto>();
        var stripe = await _sdk.GetCouponAsync(coupon.StripeCouponId!, ct);
        if (stripe.Deleted == true)
        {
            differences.Add(new("coupon", coupon.StripeCouponId, "deleted in Stripe"));
            return differences;
        }

        differences.AddRange(CouponTermDifferences(coupon, stripe, null));
        if (!string.Equals(stripe.Name, StripeName(coupon.Name), StringComparison.Ordinal))
            differences.Add(new("name", coupon.Name, stripe.Name));
        if (coupon.Status == PackageCatalogConstants.Statuses.Active && !stripe.Valid)
            differences.Add(new("valid", "true", "false"));

        if (!string.IsNullOrWhiteSpace(coupon.StripePromotionCodeId))
        {
            var promotion = await _sdk.GetPromotionCodeAsync(coupon.StripePromotionCodeId, ct);
            if (!string.Equals(promotion.Code, coupon.Code, StringComparison.OrdinalIgnoreCase))
                differences.Add(new("code", coupon.Code, promotion.Code));
            var wantActive = coupon.Status == PackageCatalogConstants.Statuses.Active;
            if (promotion.Active != wantActive)
                differences.Add(new("code.active", Bool(wantActive), Bool(promotion.Active)));
        }
        else if (!string.IsNullOrWhiteSpace(coupon.Code))
        {
            differences.Add(new("code", coupon.Code, null));
        }

        return differences;
    }

    private async Task CompareProductAsync(
        string productId, string name, string? description, bool live, List<StripeDriftItemDto> differences, CancellationToken ct)
    {
        var product = await _sdk.GetProductAsync(productId, ct);
        if (!string.Equals(product.Name, StripeName(name), StringComparison.Ordinal))
            differences.Add(new("name", name, product.Name));
        if (!string.IsNullOrWhiteSpace(description) && !string.Equals(product.Description, description, StringComparison.Ordinal))
            differences.Add(new("description", description, product.Description));
        if (product.Active != live)
            differences.Add(new("product.active", Bool(live), Bool(product.Active)));
    }

    private async Task ComparePriceAsync(
        IReadOnlyDictionary<string, string> ids,
        string key,
        decimal? amount,
        string currency,
        string? interval,
        bool live,
        List<StripeDriftItemDto> differences,
        CancellationToken ct)
    {
        var field = $"price.{key}";
        if (!ids.TryGetValue(key, out var priceId))
        {
            if (amount is { } missing)
                differences.Add(new(field, Money(missing, currency), null));
            return;
        }

        var price = await _sdk.GetPriceAsync(priceId, ct);
        if (amount is not { } wanted)
        {
            if (price.Active) differences.Add(new(field, null, StripeMoney(price)));
            return;
        }

        if (price.UnitAmount != StripePaymentService.ToMinorUnits(wanted, currency)
            || !string.Equals(price.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add(new(field, Money(wanted, currency), StripeMoney(price)));
        }

        if (!string.Equals(price.Recurring?.Interval, interval, StringComparison.Ordinal))
            differences.Add(new($"{field}.interval", interval, price.Recurring?.Interval));
        if (price.Active != live)
            differences.Add(new($"{field}.active", Bool(live), Bool(price.Active)));
    }

    /// <summary>
    /// The immutable terms of a Stripe coupon against ours. A non-empty result means a sync must
    /// create a new Stripe coupon. <paramref name="productIds"/> null skips the product check
    /// (drift reads it from what was synced).
    /// </summary>
    private static List<StripeDriftItemDto> CouponTermDifferences(
        Domain.Entities.Coupon coupon, Coupon stripe, IReadOnlyCollection<string>? productIds)
    {
        var differences = new List<StripeDriftItemDto>();
        if (coupon.DiscountType == PackageCatalogConstants.DiscountTypes.Percent)
        {
            if (stripe.PercentOff != coupon.PercentOff)
                differences.Add(new("percent_off", Num(coupon.PercentOff), Num(stripe.PercentOff)));
        }
        else
        {
            var currency = coupon.AmountOffCurrency ?? PackageCatalogConstants.Currencies.Vnd;
            if (stripe.AmountOff != StripePaymentService.ToMinorUnits(coupon.AmountOff ?? 0, currency)
                || !string.Equals(stripe.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add(new("amount_off", Money(coupon.AmountOff ?? 0, currency),
                    stripe.AmountOff is { } off ? Money(FromMinor(off, stripe.Currency ?? currency), stripe.Currency ?? currency) : null));
            }
        }

        if (!string.Equals(stripe.Duration, coupon.Duration, StringComparison.Ordinal))
            differences.Add(new("duration", coupon.Duration, stripe.Duration));
        if ((stripe.DurationInMonths ?? 0) != (coupon.DurationInMonths ?? 0))
            differences.Add(new("duration_in_months", Num(coupon.DurationInMonths), Num(stripe.DurationInMonths)));
        if ((stripe.MaxRedemptions ?? 0) != (coupon.MaxRedemptions ?? 0))
            differences.Add(new("max_redemptions", Num(coupon.MaxRedemptions), Num(stripe.MaxRedemptions)));
        if (!Same(stripe.RedeemBy, coupon.ValidUntil))
            differences.Add(new("redeem_by", Date(coupon.ValidUntil), Date(stripe.RedeemBy)));

        if (productIds is not null)
        {
            var wanted = coupon.AppliesToTypes.Contains(PackageCatalogConstants.ItemTypes.Plan)
                ? new List<string>()
                : productIds.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
            var actual = stripe.AppliesTo?.Products?.OrderBy(id => id, StringComparer.Ordinal).ToList() ?? new List<string>();
            if (!wanted.SequenceEqual(actual))
                differences.Add(new("applies_to", string.Join(',', wanted), string.Join(',', actual)));
        }

        return differences;
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static Dictionary<string, string> Metadata(string type, Guid id, string label) => new()
    {
        [PackageCatalogConstants.StripeMetadata.CatalogItemType] = type,
        [PackageCatalogConstants.StripeMetadata.CatalogItemId] = id.ToString(),
        ["warptalk_label"] = label.Length > 450 ? label[..450] : label,
    };

    /// <summary>Stripe caps names; ours are shorter, but never let a name fail a sync.</summary>
    private static string StripeName(string name) => name.Length > 250 ? name[..250] : name;

    private static bool Same(DateTime? a, DateTime? b) =>
        (a is null && b is null)
        || (a is { } x && b is { } y && Math.Abs((x.ToUniversalTime() - y.ToUniversalTime()).TotalSeconds) < 1);

    private static decimal FromMinor(long amount, string currency) =>
        PackageCatalogConstants.Currencies.IsZeroDecimal(currency) ? amount : amount / 100m;

    private static string Money(decimal amount, string currency) =>
        $"{amount.ToString(PackageCatalogConstants.Currencies.IsZeroDecimal(currency) ? "0" : "0.00", CultureInfo.InvariantCulture)} {currency.ToUpperInvariant()}";

    private static string? StripeMoney(Price price) =>
        price.UnitAmount is { } minor ? Money(FromMinor(minor, price.Currency), price.Currency) : null;

    private static string? Num(decimal? value) => value?.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? Num(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Date(DateTime? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    [GeneratedRegex(@"\b(sk|rk|pk)_(live|test)_[A-Za-z0-9*]+")]
    private static partial Regex KeyPattern();

    /// <summary>Stripe's error text, with anything shaped like an API key removed, and bounded.</summary>
    public static string Redact(Exception ex)
    {
        var message = ex is StripeException stripe && !string.IsNullOrWhiteSpace(stripe.StripeError?.Message)
            ? stripe.StripeError.Message
            : ex.Message;
        message = KeyPattern().Replace(message, "[redacted key]");
        return message.Length > MaxErrorLength ? message[..MaxErrorLength] : message;
    }
}
