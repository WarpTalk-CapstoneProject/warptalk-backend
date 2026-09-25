using System;
using System.Collections.Generic;
using FluentAssertions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.PackageCatalog;

/// <summary>
/// G11: the server-side rules of /admin/packages. The admin UI checks the same things for comfort;
/// these are the checks that count, so each rule the owner named has a test here.
/// </summary>
public class PackageCatalogRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CreditPackRequest Pack(
        decimal? vnd = 99_000m,
        decimal? usd = 4.99m,
        string visibility = PackageCatalogConstants.Visibility.Public,
        IReadOnlyList<Guid>? plans = null,
        IReadOnlyList<Guid>? workspaces = null,
        int credits = 10_000,
        int bonus = 0,
        int? validity = null,
        DateTime? from = null,
        DateTime? until = null,
        string status = PackageCatalogConstants.Statuses.Active,
        int? perWorkspace = null,
        int? total = null) =>
        new("starter-pack", "Starter pack", null, credits, bonus, vnd, usd, validity, visibility,
            plans, workspaces, perWorkspace, total, from, until, status, 0);

    [Fact]
    public void A_well_formed_pack_is_accepted() =>
        PackageCatalogRules.ValidateCreditPack(Pack()).Should().BeNull();

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Negative_or_zero_prices_are_refused(decimal price) =>
        PackageCatalogRules.ValidateCreditPack(Pack(vnd: price)).Should().NotBeNull();

    [Fact]
    public void A_pack_needs_a_price_in_at_least_one_currency() =>
        PackageCatalogRules.ValidateCreditPack(Pack(vnd: null, usd: null)).Should().Contain("at least one currency");

    [Fact]
    public void Vnd_is_whole_dong() =>
        PackageCatalogRules.ValidateCreditPack(Pack(vnd: 99_000.5m)).Should().Contain("whole dong");

    [Fact]
    public void Usd_has_at_most_two_decimals() =>
        PackageCatalogRules.ValidateCreditPack(Pack(usd: 4.999m)).Should().Contain("two decimals");

    [Fact]
    public void A_price_below_what_Stripe_can_charge_is_refused()
    {
        PackageCatalogRules.ValidateCreditPack(Pack(usd: 0.49m)).Should().Contain("smallest amount Stripe");
        PackageCatalogRules.ValidateCreditPack(Pack(vnd: 5_000m)).Should().Contain("smallest amount Stripe");
    }

    [Fact]
    public void Restricted_visibility_needs_its_list()
    {
        PackageCatalogRules.ValidateCreditPack(Pack(visibility: PackageCatalogConstants.Visibility.Plans)).Should().NotBeNull();
        PackageCatalogRules.ValidateCreditPack(Pack(visibility: PackageCatalogConstants.Visibility.Workspaces)).Should().NotBeNull();
        PackageCatalogRules.ValidateCreditPack(Pack(visibility: PackageCatalogConstants.Visibility.Plans, plans: [Guid.NewGuid()])).Should().BeNull();
        PackageCatalogRules.ValidateCreditPack(Pack(visibility: "everyone")).Should().NotBeNull();
    }

    [Fact]
    public void The_active_window_must_be_ordered() =>
        PackageCatalogRules.ValidateCreditPack(Pack(from: Now, until: Now.AddDays(-1))).Should().NotBeNull();

    [Fact]
    public void Archived_is_not_a_status_a_form_can_save() =>
        PackageCatalogRules.ValidateCreditPack(Pack(status: PackageCatalogConstants.Statuses.Archived)).Should().Contain("archive action");

    [Fact]
    public void The_per_workspace_limit_cannot_exceed_the_total() =>
        PackageCatalogRules.ValidateCreditPack(Pack(perWorkspace: 5, total: 2)).Should().NotBeNull();

    private static AddonRequest Addon(
        string key = EntitlementConstants.Keys.MaxParticipants,
        int units = 10,
        int min = 1,
        int max = 5,
        decimal? monthlyVnd = 50_000m) =>
        new("extra-participants", "Extra participants", null, "participant", key, units,
            monthlyVnd, null, null, null, min, max, null, PackageCatalogConstants.Statuses.Active, 0);

    [Fact]
    public void An_addon_must_raise_an_enforced_entitlement()
    {
        PackageCatalogRules.ValidateAddon(Addon()).Should().BeNull();
        PackageCatalogRules.ValidateAddon(Addon(key: "storage_gb")).Should().Contain("enforced entitlements");
        PackageCatalogRules.ValidateAddon(Addon(key: "recording_hours")).Should().NotBeNull();
    }

    [Fact]
    public void A_capability_addon_is_one_unit_bought_once()
    {
        PackageCatalogRules.ValidateAddon(Addon(key: EntitlementConstants.Keys.VoiceClone, units: 1, min: 1, max: 1)).Should().BeNull();
        PackageCatalogRules.ValidateAddon(Addon(key: EntitlementConstants.Keys.VoiceClone, units: 3, max: 1)).Should().NotBeNull();
        PackageCatalogRules.ValidateAddon(Addon(key: EntitlementConstants.Keys.VoiceClone, units: 1, max: 4)).Should().NotBeNull();
    }

    [Fact]
    public void Addon_quantities_must_be_ordered() =>
        PackageCatalogRules.ValidateAddon(Addon(min: 3, max: 2)).Should().NotBeNull();

    private static CouponRequest CouponReq(
        string? code = "LAUNCH20",
        string type = PackageCatalogConstants.DiscountTypes.Percent,
        decimal? percent = 20m,
        decimal? amount = null,
        string? currency = null,
        string duration = PackageCatalogConstants.Durations.Once,
        int? months = null,
        bool autoApply = false,
        int? max = null,
        int perWorkspace = 1) =>
        new(code, "Launch", type, percent, amount, currency, [PackageCatalogConstants.ItemTypes.CreditPack], null,
            duration, months, max, perWorkspace, null, null, autoApply, PackageCatalogConstants.Statuses.Active);

    [Fact]
    public void Coupon_discounts_are_bounded()
    {
        PackageCatalogRules.ValidateCoupon(CouponReq()).Should().BeNull();
        PackageCatalogRules.ValidateCoupon(CouponReq(percent: 0)).Should().NotBeNull();
        PackageCatalogRules.ValidateCoupon(CouponReq(percent: 101)).Should().NotBeNull();
        PackageCatalogRules.ValidateCoupon(CouponReq(type: PackageCatalogConstants.DiscountTypes.Fixed, percent: null, amount: -5, currency: "usd")).Should().NotBeNull();
        PackageCatalogRules.ValidateCoupon(CouponReq(type: PackageCatalogConstants.DiscountTypes.Fixed, percent: null, amount: 5)).Should().Contain("currency");
    }

    [Fact]
    public void A_coupon_needs_a_code_or_must_auto_apply()
    {
        PackageCatalogRules.ValidateCoupon(CouponReq(code: null)).Should().NotBeNull();
        PackageCatalogRules.ValidateCoupon(CouponReq(code: null, autoApply: true)).Should().BeNull();
        PackageCatalogRules.ValidateCoupon(CouponReq(code: "a b")).Should().NotBeNull();
    }

    [Fact]
    public void Only_a_repeating_coupon_has_months()
    {
        PackageCatalogRules.ValidateCoupon(CouponReq(duration: PackageCatalogConstants.Durations.Repeating)).Should().NotBeNull();
        PackageCatalogRules.ValidateCoupon(CouponReq(duration: PackageCatalogConstants.Durations.Repeating, months: 3)).Should().BeNull();
        PackageCatalogRules.ValidateCoupon(CouponReq(months: 3)).Should().NotBeNull();
    }

    // ---- Coupon evaluation ------------------------------------------------------------------

    private static Coupon Live(
        decimal? percent = 20m,
        decimal? amount = null,
        string? currency = null,
        string[]? types = null,
        Guid[]? ids = null,
        string? stripeCouponId = null,
        int? max = null,
        int perWorkspace = 1,
        DateTime? from = null,
        DateTime? until = null,
        bool autoApply = false) => new()
        {
            Id = Guid.NewGuid(),
            Code = "X",
            Name = "X",
            DiscountType = amount is null ? PackageCatalogConstants.DiscountTypes.Percent : PackageCatalogConstants.DiscountTypes.Fixed,
            PercentOff = amount is null ? percent : null,
            AmountOff = amount,
            AmountOffCurrency = currency,
            AppliesToTypes = types ?? [PackageCatalogConstants.ItemTypes.CreditPack],
            AppliesToIds = ids ?? [],
            StripeCouponId = stripeCouponId,
            MaxRedemptions = max,
            PerWorkspaceLimit = perWorkspace,
            ValidFrom = from,
            ValidUntil = until,
            AutoApply = autoApply,
            Status = PackageCatalogConstants.Statuses.Active,
        };

    private static PackageCatalogRules.CouponContext Ctx(
        decimal list = 100_000m,
        string currency = "vnd",
        string type = PackageCatalogConstants.ItemTypes.CreditPack,
        Guid? itemId = null,
        bool recurring = false,
        int total = 0,
        int byWorkspace = 0) => new(type, itemId, currency, list, recurring, Now, total, byWorkspace);

    [Fact]
    public void A_percent_coupon_takes_its_share_rounded_to_the_currency()
    {
        var evaluation = PackageCatalogRules.Evaluate(Live(percent: 15m), Ctx(list: 99_999m));
        evaluation.Ok.Should().BeTrue();
        evaluation.Discount.Should().Be(15_000m); // 14,999.85 → whole dong
        evaluation.Total.Should().Be(84_999m);
    }

    [Fact]
    public void A_fixed_coupon_only_applies_in_its_own_currency()
    {
        var coupon = Live(amount: 1m, currency: "usd");
        PackageCatalogRules.Evaluate(coupon, Ctx(list: 10m, currency: "usd")).Total.Should().Be(9m);
        PackageCatalogRules.Evaluate(coupon, Ctx()).Error.Should().Contain("fixed USD discount");
    }

    [Fact]
    public void A_coupon_outside_its_window_or_scope_does_not_apply()
    {
        PackageCatalogRules.Evaluate(Live(from: Now.AddDays(1)), Ctx()).Ok.Should().BeFalse();
        PackageCatalogRules.Evaluate(Live(until: Now), Ctx()).Ok.Should().BeFalse();
        PackageCatalogRules.Evaluate(Live(types: [PackageCatalogConstants.ItemTypes.Addon]), Ctx()).Error
            .Should().Be(PackageCatalogConstants.Errors.CouponNotApplicable);
        PackageCatalogRules.Evaluate(Live(ids: [Guid.NewGuid()]), Ctx(itemId: Guid.NewGuid())).Ok.Should().BeFalse();
    }

    [Fact]
    public void Redemption_limits_are_enforced()
    {
        PackageCatalogRules.Evaluate(Live(max: 10), Ctx(total: 10)).Error.Should().Be(PackageCatalogConstants.Errors.CouponExhausted);
        PackageCatalogRules.Evaluate(Live(perWorkspace: 1), Ctx(byWorkspace: 1)).Error.Should().Be(PackageCatalogConstants.Errors.CouponWorkspaceLimit);
    }

    [Fact]
    public void A_recurring_purchase_only_takes_a_coupon_Stripe_knows()
    {
        var types = new[] { PackageCatalogConstants.ItemTypes.Addon };
        PackageCatalogRules.Evaluate(Live(types: types), Ctx(type: PackageCatalogConstants.ItemTypes.Addon, recurring: true))
            .Error.Should().Be(PackageCatalogConstants.Errors.CouponRecurringNeedsStripe);
        PackageCatalogRules.Evaluate(Live(types: types, stripeCouponId: "co_1"), Ctx(type: PackageCatalogConstants.ItemTypes.Addon, recurring: true))
            .Ok.Should().BeTrue();
    }

    [Fact]
    public void A_one_off_total_must_stay_chargeable() =>
        PackageCatalogRules.Evaluate(Live(percent: 95m), Ctx(list: 100_000m)).Error.Should().Be(PackageCatalogConstants.Errors.CouponBelowMinimum);

    [Fact]
    public void Stacking_rule_one_coupon_the_best_campaign_wins()
    {
        var small = Live(percent: 10m, autoApply: true);
        var large = Live(percent: 25m, autoApply: true);
        var notCampaign = Live(percent: 50m);

        var best = PackageCatalogRules.BestAutoApply([small, large, notCampaign], _ => Ctx());

        best.Should().NotBeNull();
        best!.Value.Coupon.Should().BeSameAs(large);
        best.Value.Evaluation.Discount.Should().Be(25_000m);
    }

    // ---- Stripe sync state ------------------------------------------------------------------

    [Fact]
    public void A_price_edit_makes_a_synced_pack_outdated_but_a_sort_change_does_not()
    {
        var pack = new CreditPack { Name = "P", PriceVnd = 100_000m, Status = PackageCatalogConstants.Statuses.Active };
        var synced = PackageCatalogRules.Fingerprint(pack);

        pack.SortOrder = 9;
        pack.MaxPerWorkspace = 3;
        PackageCatalogRules.SyncState("prod_1", null, synced, PackageCatalogRules.Fingerprint(pack))
            .Should().Be(PackageCatalogConstants.SyncStates.Synced);

        pack.PriceVnd = 120_000m;
        PackageCatalogRules.SyncState("prod_1", null, synced, PackageCatalogRules.Fingerprint(pack))
            .Should().Be(PackageCatalogConstants.SyncStates.Outdated);
    }

    [Fact]
    public void Sync_state_reports_errors_and_never_synced_items()
    {
        PackageCatalogRules.SyncState(null, null, null, "h").Should().Be(PackageCatalogConstants.SyncStates.NotSynced);
        PackageCatalogRules.SyncState("prod_1", "declined", "h", "h").Should().Be(PackageCatalogConstants.SyncStates.Error);
    }

    [Fact]
    public void A_duplicate_gets_a_free_slug()
    {
        var taken = new HashSet<string> { "starter-copy", "starter-copy-2" };
        PackageCatalogRules.CopySlug("starter", taken.Contains).Should().Be("starter-copy-3");
    }

    [Fact]
    public void Price_ids_round_trip_through_jsonb()
    {
        var json = PackageCatalogRules.WritePriceIds(new Dictionary<string, string> { ["vnd"] = "price_a", ["usd"] = "price_b" });
        PackageCatalogRules.ReadPriceIds(json).Should().BeEquivalentTo(new Dictionary<string, string> { ["usd"] = "price_b", ["vnd"] = "price_a" });
        PackageCatalogRules.ReadPriceIds("not json").Should().BeEmpty();
    }
}
