using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.BillingService.Domain.Constants;

/// <summary>
/// G11 — the vocabulary of everything WarpTalk sells besides a base plan: one-off credit packs,
/// recurring add-ons on top of a plan, and coupons that discount either.
///
/// Every string here is persisted (a CHECK constraint in migration 20260925160000 holds the same
/// list) or travels on a Stripe object's metadata, so treat them as a contract: add, never rename.
/// </summary>
public static class PackageCatalogConstants
{
    /// <summary>Lifecycle of a sellable item. Only <see cref="Active"/> is offered to customers.</summary>
    public static class Statuses
    {
        public const string Draft = "draft";
        public const string Active = "active";
        public const string Archived = "archived";

        public static readonly string[] All = [Draft, Active, Archived];
    }

    /// <summary>Who may see and buy a credit pack.</summary>
    public static class Visibility
    {
        /// <summary>Every workspace with a subscription.</summary>
        public const string Public = "public";

        /// <summary>Only workspaces whose current plan is in <c>eligible_plan_ids</c>.</summary>
        public const string Plans = "plans";

        /// <summary>Only the workspaces listed in <c>eligible_workspace_ids</c> (a negotiated pack).</summary>
        public const string Workspaces = "workspaces";

        public static readonly string[] All = [Public, Plans, Workspaces];
    }

    /// <summary>What a coupon can discount. Also the <c>item_type</c> of a redemption.</summary>
    public static class ItemTypes
    {
        public const string Plan = "plan";
        public const string CreditPack = "credit_pack";
        public const string Addon = "addon";

        public static readonly string[] All = [Plan, CreditPack, Addon];
    }

    public static class DiscountTypes
    {
        public const string Percent = "percent";
        public const string Fixed = "fixed";

        public static readonly string[] All = [Percent, Fixed];
    }

    /// <summary>Stripe's own coupon durations, so a synced coupon means the same thing on both sides.</summary>
    public static class Durations
    {
        /// <summary>The first payment only.</summary>
        public const string Once = "once";

        /// <summary>Every payment for <c>duration_in_months</c> months.</summary>
        public const string Repeating = "repeating";

        /// <summary>Every payment for as long as the subscription lasts.</summary>
        public const string Forever = "forever";

        public static readonly string[] All = [Once, Repeating, Forever];
    }

    /// <summary>State of a workspace's add-on subscription.</summary>
    public static class WorkspaceAddonStatuses
    {
        public const string Active = "active";

        /// <summary>Cancelled at period end: still granted until <c>current_period_end</c>.</summary>
        public const string Cancelling = "cancelling";

        public const string Cancelled = "cancelled";

        public static readonly string[] All = [Active, Cancelling, Cancelled];

        /// <summary>The statuses that still grant the add-on's entitlement.</summary>
        public static readonly string[] Granting = [Active, Cancelling];
    }

    /// <summary>
    /// The entitlements an add-on may raise. Each is a key <see cref="EntitlementConstants.Keys"/>
    /// already resolves AND a consumer already enforces — an add-on whose grant nothing reads would
    /// be money taken for nothing, which is precisely the WT-429 failure. A numeric key adds
    /// <c>units_per_quantity × quantity</c> on top of the plan; a boolean key switches the
    /// capability on.
    ///
    /// Deliberately absent: voice-clone slots, storage and recording hours. None of them is an
    /// entitlement anything enforces today, so none of them can be sold as one.
    /// </summary>
    public static class AddonEntitlements
    {
        public static readonly string[] All =
        [
            EntitlementConstants.Keys.MaxParticipants,
            EntitlementConstants.Keys.MaxLanguages,
            EntitlementConstants.Keys.MaxActiveRooms,
            EntitlementConstants.Keys.VoiceClone,
            EntitlementConstants.Keys.AiAssistant,
            EntitlementConstants.Keys.Glossary,
        ];

        public static bool IsAllowed(string? key) =>
            key is not null && All.Contains(key, StringComparer.Ordinal);
    }

    /// <summary>Currencies an item can be priced in, lower-case as Stripe spells them.</summary>
    public static class Currencies
    {
        public const string Vnd = PaymentConstants.Currencies.Vnd;
        public const string Usd = PaymentConstants.Currencies.Usd;

        public static readonly string[] All = [Vnd, Usd];

        /// <summary>
        /// The smallest amount Stripe will charge in each currency (Stripe's documented minimum
        /// charge; VND is zero-decimal). A price or a discounted total below it cannot be paid.
        /// </summary>
        public static decimal MinimumCharge(string currency) =>
            string.Equals(currency, Vnd, StringComparison.OrdinalIgnoreCase) ? 12_000m : 0.50m;

        public static bool IsZeroDecimal(string currency) =>
            string.Equals(currency, Vnd, StringComparison.OrdinalIgnoreCase);

        public static string? Normalize(string? currency)
        {
            var lower = currency?.Trim().ToLowerInvariant();
            return lower is not null && All.Contains(lower, StringComparer.Ordinal) ? lower : null;
        }
    }

    /// <summary>Keys of the <c>stripe_price_ids</c> jsonb map.</summary>
    public static class PriceKeys
    {
        public static string Pack(string currency) => currency.ToLowerInvariant();

        public static string Addon(string billingCycle, string currency) =>
            $"{billingCycle.ToLowerInvariant()}_{currency.ToLowerInvariant()}";
    }

    /// <summary>Stripe session / subscription metadata written by a catalog checkout.</summary>
    public static class StripeMetadata
    {
        public const string PackageId = "PackageId";
        public const string CouponId = "CouponId";
        public const string Quantity = "Quantity";
        public const string ListPrice = "ListPrice";

        /// <summary>Stamped on every Stripe Product/Price/Coupon the admin sync creates.</summary>
        public const string CatalogItemId = "warptalk_catalog_id";
        public const string CatalogItemType = "warptalk_catalog_type";
    }

    /// <summary>Ledger reference types for rows a catalog purchase writes.</summary>
    public static class ReferenceTypes
    {
        public const string CreditPackPurchase = "credit_pack_purchase";
        public const string CreditPackExpiry = "credit_pack_expiry";
    }

    /// <summary>Sync state shown next to each item in the admin.</summary>
    public static class SyncStates
    {
        /// <summary>Never pushed to Stripe. Checkout still works, with inline price data.</summary>
        public const string NotSynced = "not_synced";

        /// <summary>Stripe has what the database has.</summary>
        public const string Synced = "synced";

        /// <summary>The item was edited after its last sync; Stripe still has the old terms.</summary>
        public const string Outdated = "outdated";

        /// <summary>The last sync failed; <c>stripe_sync_error</c> says why.</summary>
        public const string Error = "error";
    }

    public static class Limits
    {
        public const int SlugMaxLength = 60;
        public const int NameMaxLength = 100;
        public const int DescriptionMaxLength = 500;
        public const int CodeMaxLength = 40;
        public const int UnitLabelMaxLength = 40;
        public const int MaxCredits = 100_000_000;
        public const int MaxValidityDays = 3650;
        public const int MaxAddonQuantity = 1000;
        public const decimal MaxPriceVnd = 10_000_000_000m;
        public const decimal MaxPriceUsd = 1_000_000m;
    }

    public static class Errors
    {
        public const string NotFound = "The package was not found.";
        public const string SlugTaken = "Another package already uses the slug '{0}'.";
        public const string CodeTaken = "Another coupon already uses the code '{0}'.";
        public const string Archived = "An archived package cannot be edited. Unarchive it first.";
        public const string NotAvailable = "This package is not available to this workspace.";
        public const string CurrencyNotOffered = "This package has no {0} price.";
        public const string NoSubscription = "Credits are added to the workspace's subscription, and this workspace has none yet.";
        public const string PurchaseLimitReached = "This workspace has already bought this package the maximum number of times.";
        public const string SoldOut = "This package has sold out.";
        public const string AddonAlreadyActive = "This workspace already has this add-on. Cancel it before buying a different quantity.";
        public const string AddonQuantityOutOfRange = "Quantity must be between {0} and {1}.";
        public const string AddonCycleNotOffered = "This add-on has no {0} {1} price.";
        public const string AddonRequiresPlan = "Add-ons sit on top of a paid plan, and this workspace has no active one.";
        public const string CouponInvalid = "This coupon code is not valid.";
        public const string CouponNotApplicable = "This coupon does not apply to this purchase.";
        public const string CouponExhausted = "This coupon has reached its redemption limit.";
        public const string CouponWorkspaceLimit = "This workspace has already used this coupon.";
        public const string CouponCurrencyMismatch = "This coupon is a fixed {0} discount and cannot be used on a {1} purchase.";
        public const string CouponBelowMinimum = "The discounted total would be below the smallest amount Stripe can charge.";
        public const string CouponRecurringNeedsStripe =
            "A coupon on a recurring purchase must be synced to Stripe first, so Stripe applies it to each renewal.";
        public const string CouponNotForTopUp = "Coupons apply to credit packs, add-ons and plans, not to a custom top-up.";
        public const string StripeNotConfigured = "Stripe is not configured on this environment.";
    }
}
