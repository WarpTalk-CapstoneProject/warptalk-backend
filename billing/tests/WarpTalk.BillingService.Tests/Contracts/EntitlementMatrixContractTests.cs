using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;

namespace WarpTalk.BillingService.Tests.Contracts;

/// <summary>
/// WT-263: pins the entitlement matrix — plan × capability → expected outcome.
///
/// In the spirit of warptalk-web/scripts/check-create-room-language-contract.mjs: a decision that
/// several parts of the product depend on, written down once, in a place CI runs, so that changing
/// it requires SAYING you changed it. This invariant has the same shape as the translation
/// auto-start contract, which flipped twice before it was pinned.
///
/// It fails in three distinct ways, each deliberate:
///   1. A key added to or removed from the resolver without updating ExpectedMatrix — so a new
///      capability cannot ship unenforced and undocumented.
///   2. A cell whose resolved VALUE changes — a plan quietly gaining or losing a capability.
///   3. A cell whose PROVENANCE changes — the value is right but a different layer decided it,
///      which means the resolution order moved.
///
/// This lives in the billing test project, which runs under `dotnet test warptalk-backend.slnx`.
/// </summary>
public class EntitlementMatrixContractTests
{
    private static readonly DateTime ResolvedAt = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The pinned matrix. Rows are representative plan shapes, columns are entitlement keys, cells
    /// are "value|source". EDIT THIS DELIBERATELY — a change here is a product decision about what a
    /// plan grants, not a test fixup.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> ExpectedMatrix = new()
    {
        // No plan at all, or a plan whose subscription is not in force: the platform floor. This row
        // is what a workspace with no live subscription gets, and it must stay permissive enough
        // that "no subscription" never means "no meetings".
        ["no-active-plan"] = new()
        {
            ["max_languages"] = "2|platform_default",
            ["max_active_rooms"] = "5|platform_default",
            ["max_participants"] = "2|platform_default",
            ["voice_clone"] = "false|platform_default",
            ["ai_assistant"] = "false|platform_default",
            ["glossary"] = "false|platform_default"
        },

        // The seeded Enterprise plan: max_languages at the platform ceiling (3), every capability on,
        // and the full self-service room range (50).
        ["enterprise"] = new()
        {
            ["max_languages"] = "3|plan:enterprise",
            ["max_active_rooms"] = "50|plan:enterprise",
            ["max_participants"] = "500|plan:enterprise",
            ["voice_clone"] = "true|plan:enterprise",
            ["ai_assistant"] = "true|plan:enterprise",
            ["glossary"] = "true|plan:enterprise"
        },

        // A mid-tier shape: a real plan row, but not everything switched on. Guards against a
        // regression where "has a plan" is mistaken for "has everything".
        ["startup"] = new()
        {
            ["max_languages"] = "2|plan:startup",
            ["max_active_rooms"] = "10|plan:startup",
            ["max_participants"] = "25|plan:startup",
            ["voice_clone"] = "false|plan:startup",
            ["ai_assistant"] = "true|plan:startup",
            ["glossary"] = "false|plan:startup"
        }
    };

    private static Plan? PlanFor(string row) => row switch
    {
        "no-active-plan" => null,
        "enterprise" => new Plan
        {
            Slug = "enterprise",
            MaxLanguages = SubscriptionConstants.EnterpriseBaseline.MaxLanguages,
            MaxActiveRooms = 50,
            MaxParticipants = SubscriptionConstants.EnterpriseBaseline.MaxParticipants,
            VoiceCloneEnabled = true,
            AiAssistantEnabled = true,
            GlossaryEnabled = true
        },
        "startup" => new Plan
        {
            Slug = "startup",
            MaxLanguages = 2,
            MaxActiveRooms = 10,
            MaxParticipants = 25,
            VoiceCloneEnabled = false,
            AiAssistantEnabled = true,
            GlossaryEnabled = false
        },
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown matrix row.")
    };

    [Fact]
    public void Matrix_CoversExactlyTheKeysTheResolverProduces()
    {
        foreach (var (row, expected) in ExpectedMatrix)
        {
            expected.Keys.Should().BeEquivalentTo(
                EntitlementConstants.Keys.All,
                because:
                    $"row '{row}' of the pinned entitlement matrix must list every entitlement key and no others. " +
                    "A key added to EntitlementConstants.Keys.All without a decision about what each plan grants " +
                    "would ship a capability that nothing enforces.");
        }
    }

    [Theory]
    [InlineData("no-active-plan")]
    [InlineData("enterprise")]
    [InlineData("startup")]
    public void Matrix_ResolvesToThePinnedValuesAndProvenance(string row)
    {
        var plan = PlanFor(row);
        var inputs = new EntitlementResolutionInputs(
            plan,
            HasActiveSubscription: plan != null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        var map = EntitlementResolver.Resolve(Guid.NewGuid(), inputs, ResolvedAt);

        var actual = map.Entitlements.ToDictionary(
            entitlement => entitlement.Key,
            entitlement => $"{entitlement.Value}|{entitlement.Source}",
            StringComparer.Ordinal);

        actual.Should().BeEquivalentTo(
            ExpectedMatrix[row],
            because:
                $"the '{row}' row of the entitlement matrix is a pinned product decision. " +
                "If this changed on purpose, update ExpectedMatrix in the same commit and say so in the " +
                "message; if it changed by accident, the resolution order or a plan default has drifted.");
    }

    /// <summary>
    /// The tighten-not-loosen invariant, pinned alongside the matrix because it is the rule that
    /// makes every cell above an upper bound rather than a fixed value.
    /// </summary>
    [Fact]
    public void Matrix_CellsAreCeilings_AWorkspaceMayTightenButNeverLoosenThem()
    {
        var plan = PlanFor("startup")!;

        var tightened = EntitlementResolver.Resolve(
            Guid.NewGuid(),
            new EntitlementResolutionInputs(
                plan,
                true,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["max_active_rooms"] = "3" }),
            ResolvedAt);
        tightened.Number("max_active_rooms").Should().Be(3);
        tightened.Source("max_active_rooms").Should().Be(EntitlementConstants.Sources.WorkspaceOverride);

        var loosened = EntitlementResolver.Resolve(
            Guid.NewGuid(),
            new EntitlementResolutionInputs(
                plan,
                true,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["max_active_rooms"] = "45" }),
            ResolvedAt);
        loosened.Number("max_active_rooms").Should().Be(10);
        loosened.Source("max_active_rooms").Should().Be("plan:startup");
    }

    /// <summary>
    /// The key names are a wire contract shared with every consumer's snapshot table. Renaming one
    /// silently retargets enforcement at a key that no longer exists, so the spellings are pinned
    /// literally rather than through the constants that define them.
    /// </summary>
    [Fact]
    public void EntitlementKeyNames_AreAWireContract()
    {
        EntitlementConstants.Keys.All.Should().BeEquivalentTo(new[]
        {
            "max_languages",
            "max_active_rooms",
            "max_participants",
            "voice_clone",
            "ai_assistant",
            "glossary"
        });

        EntitlementConstants.Sources.PlatformDefault.Should().Be("platform_default");
        EntitlementConstants.Sources.ContractOverride.Should().Be("contract_override");
        EntitlementConstants.Sources.WorkspaceOverride.Should().Be("workspace_override");
        EntitlementConstants.Sources.Plan("enterprise").Should().Be("plan:enterprise");
    }
}
