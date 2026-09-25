using System;
using System.Collections.Generic;
using FluentAssertions;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.PackageCatalog;

/// <summary>
/// G11: purchased add-ons raise the plan's entitlements (layer 2b) — the add-on sold must be the
/// entitlement enforced, or it is money taken for nothing.
/// </summary>
public class AddonEntitlementLayerTests
{
    private static readonly DateTime At = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);

    private static Plan Plan() => new()
    {
        Id = Guid.NewGuid(),
        Slug = "startup",
        Name = "Startup",
        Tier = SubscriptionConstants.Tiers.Startup,
        MaxParticipants = 100,
        MaxLanguages = 3,
        MaxActiveRooms = 5,
        VoiceCloneEnabled = false,
    };

    private static EntitlementResolutionInputs Inputs(
        IReadOnlyList<AddonGrant> grants,
        bool active = true,
        Dictionary<string, string>? contract = null,
        Dictionary<string, string>? workspace = null) => new(
            Plan(),
            active,
            contract ?? new Dictionary<string, string>(StringComparer.Ordinal),
            workspace ?? new Dictionary<string, string>(StringComparer.Ordinal),
            grants);

    [Fact]
    public void Numeric_addons_add_to_the_plan_and_name_themselves_as_the_source()
    {
        var map = EntitlementResolver.Resolve(Guid.NewGuid(), Inputs(
        [
            new AddonGrant(EntitlementConstants.Keys.MaxParticipants, 50, "extra-participants"),
            new AddonGrant(EntitlementConstants.Keys.MaxParticipants, 25, "zz-more-participants"),
        ]), At);

        map.Number(EntitlementConstants.Keys.MaxParticipants).Should().Be(175);
        map.Source(EntitlementConstants.Keys.MaxParticipants).Should().Be("addon:extra-participants");
        map.Number(EntitlementConstants.Keys.MaxLanguages).Should().Be(3);
    }

    [Fact]
    public void A_capability_addon_switches_the_feature_on()
    {
        var map = EntitlementResolver.Resolve(Guid.NewGuid(), Inputs(
            [new AddonGrant(EntitlementConstants.Keys.VoiceClone, 1, "voice-clone")]), At);

        map.Flag(EntitlementConstants.Keys.VoiceClone).Should().BeTrue();
        map.Source(EntitlementConstants.Keys.VoiceClone).Should().Be("addon:voice-clone");
    }

    [Fact]
    public void Addons_do_nothing_without_a_live_plan()
    {
        var map = EntitlementResolver.Resolve(Guid.NewGuid(), Inputs(
            [new AddonGrant(EntitlementConstants.Keys.MaxParticipants, 50, "extra-participants")], active: false), At);

        map.Number(EntitlementConstants.Keys.MaxParticipants).Should().Be(EntitlementConstants.PlatformDefaults.MaxParticipants);
        map.Source(EntitlementConstants.Keys.MaxParticipants).Should().Be(EntitlementConstants.Sources.PlatformDefault);
    }

    [Fact]
    public void A_contract_still_outranks_an_addon_and_the_owner_can_still_tighten()
    {
        var grants = new[] { new AddonGrant(EntitlementConstants.Keys.MaxParticipants, 50, "extra-participants") };

        EntitlementResolver.Resolve(Guid.NewGuid(), Inputs(grants,
                contract: new() { [EntitlementConstants.Keys.MaxParticipants] = "500" }), At)
            .Number(EntitlementConstants.Keys.MaxParticipants).Should().Be(500);

        var tightened = EntitlementResolver.Resolve(Guid.NewGuid(), Inputs(grants,
            workspace: new() { [EntitlementConstants.Keys.MaxParticipants] = "120" }), At);
        tightened.Number(EntitlementConstants.Keys.MaxParticipants).Should().Be(120);
        tightened[EntitlementConstants.Keys.MaxParticipants].Ceiling.Should().Be("150");

        // …but the add-on ceiling is still a ceiling: 200 > 150 is a loosening and is dropped.
        EntitlementResolver.Resolve(Guid.NewGuid(), Inputs(grants,
                workspace: new() { [EntitlementConstants.Keys.MaxParticipants] = "200" }), At)
            .Number(EntitlementConstants.Keys.MaxParticipants).Should().Be(150);
    }

    [Fact]
    public void A_cancelling_addon_grants_until_its_paid_period_ends()
    {
        var row = new WorkspaceAddon
        {
            Status = PackageCatalogConstants.WorkspaceAddonStatuses.Cancelling,
            CurrentPeriodEnd = At.AddDays(3),
        };

        row.GrantsAt(At).Should().BeTrue();
        row.GrantsAt(At.AddDays(4)).Should().BeFalse();
        new WorkspaceAddon { Status = PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled }.GrantsAt(At).Should().BeFalse();
    }
}
