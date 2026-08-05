using System;
using System.Collections.Generic;
using FluentAssertions;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Entitlements;

/// <summary>
/// WT-263: the resolution ORDER and the tighten-not-loosen invariant.
///
/// These exercise the pure <see cref="EntitlementResolver.Resolve"/> because what has to hold is a
/// property of precedence, not of persistence — and because the invariant is the thing most likely
/// to be "simplified" away by a later change.
/// </summary>
public class EntitlementResolverTests
{
    private static readonly DateTime ResolvedAt = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    private static Plan PlanWith(
        string slug = "startup",
        int maxLanguages = 3,
        int maxActiveRooms = 20,
        int maxParticipants = 100,
        bool voiceClone = true,
        bool aiAssistant = true,
        bool glossary = true) => new()
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = slug,
            Tier = SubscriptionConstants.Tiers.Startup,
            MaxLanguages = maxLanguages,
            MaxActiveRooms = maxActiveRooms,
            MaxParticipants = maxParticipants,
            VoiceCloneEnabled = voiceClone,
            AiAssistantEnabled = aiAssistant,
            GlossaryEnabled = glossary
        };

    private static EntitlementResolutionInputs Inputs(
        Plan? plan = null,
        bool hasActiveSubscription = true,
        Dictionary<string, string>? contract = null,
        Dictionary<string, string>? workspace = null) => new(
            plan,
            hasActiveSubscription,
            contract ?? new Dictionary<string, string>(StringComparer.Ordinal),
            workspace ?? new Dictionary<string, string>(StringComparer.Ordinal));

    private static WorkspaceEntitlementMap Resolve(EntitlementResolutionInputs inputs) =>
        EntitlementResolver.Resolve(Guid.NewGuid(), inputs, ResolvedAt);

    // ── Level 1: platform default ─────────────────────────────────────────────

    [Fact]
    public void PlatformDefault_Wins_WhenNothingAboveItHasAnOpinion()
    {
        var map = Resolve(Inputs(plan: null, hasActiveSubscription: false));

        map.Number(EntitlementConstants.Keys.MaxLanguages)
            .Should().Be(EntitlementConstants.PlatformDefaults.MaxLanguages);
        map.Source(EntitlementConstants.Keys.MaxLanguages)
            .Should().Be(EntitlementConstants.Sources.PlatformDefault);
        map.Flag(EntitlementConstants.Keys.VoiceClone).Should().BeFalse();
        map.Source(EntitlementConstants.Keys.VoiceClone)
            .Should().Be(EntitlementConstants.Sources.PlatformDefault);
    }

    /// <summary>
    /// A plan whose subscription is not in force must not put its numbers in force. This is the
    /// WT-262 carve-out ("no subscription" must never become "no meetings") expressed as an ordinary
    /// layer rather than as a branch in the enforcement code.
    /// </summary>
    [Fact]
    public void PlanIsIgnored_WhenTheSubscriptionIsNotActive()
    {
        var map = Resolve(Inputs(PlanWith(maxLanguages: 3), hasActiveSubscription: false));

        map.Number(EntitlementConstants.Keys.MaxLanguages)
            .Should().Be(EntitlementConstants.PlatformDefaults.MaxLanguages);
        map.Source(EntitlementConstants.Keys.MaxLanguages)
            .Should().Be(EntitlementConstants.Sources.PlatformDefault);
    }

    // ── Level 2: plan ─────────────────────────────────────────────────────────

    [Fact]
    public void Plan_WinsOverPlatformDefault_AndNamesItselfInTheProvenance()
    {
        var map = Resolve(Inputs(PlanWith(slug: "enterprise", maxLanguages: 3)));

        map.Number(EntitlementConstants.Keys.MaxLanguages).Should().Be(3);
        map.Source(EntitlementConstants.Keys.MaxLanguages).Should().Be("plan:enterprise");
    }

    // ── Level 3: contract override ────────────────────────────────────────────

    [Fact]
    public void ContractOverride_WinsOverThePlan()
    {
        var map = Resolve(Inputs(
            PlanWith(maxLanguages: 3),
            contract: new Dictionary<string, string> { ["max_languages"] = "5" }));

        map.Number(EntitlementConstants.Keys.MaxLanguages).Should().Be(5);
        map.Source(EntitlementConstants.Keys.MaxLanguages)
            .Should().Be(EntitlementConstants.Sources.ContractOverride);
    }

    /// <summary>
    /// A contract may LOOSEN. It is the negotiated agreement with the platform, so it outranks the
    /// catalog row in both directions — unlike the workspace layer below it.
    /// </summary>
    [Fact]
    public void ContractOverride_MayGrantMoreThanThePlanSells()
    {
        var map = Resolve(Inputs(
            PlanWith(voiceClone: false),
            contract: new Dictionary<string, string> { ["voice_clone"] = "true" }));

        map.Flag(EntitlementConstants.Keys.VoiceClone).Should().BeTrue();
        map.Source(EntitlementConstants.Keys.VoiceClone)
            .Should().Be(EntitlementConstants.Sources.ContractOverride);
    }

    // ── Level 4: workspace self-service, and THE INVARIANT ────────────────────

    [Fact]
    public void WorkspaceOverride_MayTightenBelowThePlan()
    {
        var map = Resolve(Inputs(
            PlanWith(maxLanguages: 3),
            workspace: new Dictionary<string, string> { ["max_languages"] = "2" }));

        map.Number(EntitlementConstants.Keys.MaxLanguages).Should().Be(2);
        map.Source(EntitlementConstants.Keys.MaxLanguages)
            .Should().Be(EntitlementConstants.Sources.WorkspaceOverride);
    }

    /// <summary>
    /// THE invariant. A workspace raising a limit beyond its plan is not applied — and it is DROPPED
    /// rather than clamped, so the provenance keeps pointing at the layer that really decided.
    /// Reporting source=workspace_override for a value the workspace did not choose would make the
    /// map lie about itself.
    /// </summary>
    [Fact]
    public void WorkspaceOverride_CannotLoosenBeyondThePlan()
    {
        var map = Resolve(Inputs(
            PlanWith(slug: "startup", maxLanguages: 2),
            workspace: new Dictionary<string, string> { ["max_languages"] = "9" }));

        map.Number(EntitlementConstants.Keys.MaxLanguages).Should().Be(2);
        map.Source(EntitlementConstants.Keys.MaxLanguages).Should().Be("plan:startup");
    }

    [Fact]
    public void WorkspaceOverride_MayTurnACapabilityOff_ButNotOn()
    {
        var turnedOff = Resolve(Inputs(
            PlanWith(aiAssistant: true),
            workspace: new Dictionary<string, string> { ["ai_assistant"] = "false" }));
        turnedOff.Flag(EntitlementConstants.Keys.AiAssistant).Should().BeFalse();
        turnedOff.Source(EntitlementConstants.Keys.AiAssistant)
            .Should().Be(EntitlementConstants.Sources.WorkspaceOverride);

        var turnedOn = Resolve(Inputs(
            PlanWith(slug: "startup", aiAssistant: false),
            workspace: new Dictionary<string, string> { ["ai_assistant"] = "true" }));
        turnedOn.Flag(EntitlementConstants.Keys.AiAssistant).Should().BeFalse();
        turnedOn.Source(EntitlementConstants.Keys.AiAssistant).Should().Be("plan:startup");
    }

    /// <summary>
    /// A workspace may tighten below a CONTRACT value too, not just below the plan — the ceiling is
    /// whatever the three layers above it settled on.
    /// </summary>
    [Fact]
    public void WorkspaceOverride_TightensAgainstTheContractCeiling_NotThePlanRow()
    {
        var map = Resolve(Inputs(
            PlanWith(maxActiveRooms: 20),
            contract: new Dictionary<string, string> { ["max_active_rooms"] = "40" },
            workspace: new Dictionary<string, string> { ["max_active_rooms"] = "30" }));

        // 30 loosens the plan's 20 but tightens the contract's 40, so it is applied.
        map.Number(EntitlementConstants.Keys.MaxActiveRooms).Should().Be(30);
        map.Source(EntitlementConstants.Keys.MaxActiveRooms)
            .Should().Be(EntitlementConstants.Sources.WorkspaceOverride);
    }

    /// <summary>A value nobody can parse must never become an entitlement, in either direction.</summary>
    [Fact]
    public void UnparseableOverride_IsIgnoredRatherThanGuessed()
    {
        var map = Resolve(Inputs(
            PlanWith(slug: "startup", maxLanguages: 3),
            workspace: new Dictionary<string, string> { ["max_languages"] = "lots" }));

        map.Number(EntitlementConstants.Keys.MaxLanguages).Should().Be(3);
        map.Source(EntitlementConstants.Keys.MaxLanguages).Should().Be("plan:startup");
    }

    // ── Shape ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryKeyIsResolved_WithAValueAndAProvenance()
    {
        var map = Resolve(Inputs(PlanWith()));

        map.Entitlements.Should().HaveCount(EntitlementConstants.Keys.All.Length);
        foreach (var entitlement in map.Entitlements)
        {
            entitlement.Value.Should().NotBeNullOrWhiteSpace();
            entitlement.Source.Should().NotBeNullOrWhiteSpace();
        }
    }
}
