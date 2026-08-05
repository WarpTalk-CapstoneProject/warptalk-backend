using System;
using FluentAssertions;
using WarpTalk.BillingService.API.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;

namespace WarpTalk.BillingService.Tests.API.Mappers;

/// <summary>
/// WT-262. Before this ticket the feature-access projection hardcoded every capability flag to
/// true, wrote a constant in place of the plan's max_languages, and derived dedicated_gpu from a
/// Tier string comparison — so the quota columns on subscription.plans were never actually read by
/// anything. These pin the projection to the columns.
/// </summary>
public class GrpcFeatureAccessMapperTests
{
    private static Subscription ActiveSubscription() => new()
    {
        Id = Guid.NewGuid(),
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        IsActive = true,
        CurrentPeriodEnd = DateTime.UtcNow.AddDays(20)
    };

    /// <summary>A plan whose every gated column differs from the value the old mapper fabricated,
    /// so a regression to any hardcoded constant fails here rather than passing by coincidence.</summary>
    private static Plan NonDefaultPlan() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Custom",
        Slug = "custom",
        Tier = SubscriptionConstants.Tiers.Startup,
        MaxParticipants = 37,
        MaxLanguages = 1,
        VoiceCloneEnabled = false,
        AiAssistantEnabled = false,
        GlossaryEnabled = false,
        DedicatedGpu = true,
        Features = "{\"custom\":true}"
    };

    [Fact]
    public void FeatureAccess_ReadsEveryQuotaAndFlagFromThePlanColumns()
    {
        var response = ActiveSubscription().ToFeatureAccessResponse(NonDefaultPlan());

        response.HasActiveSubscription.Should().BeTrue();
        response.PlanTier.Should().Be(SubscriptionConstants.Tiers.Startup);
        response.MaxParticipants.Should().Be(37);
        response.MaxLanguages.Should().Be(1);
        response.VoiceCloneEnabled.Should().BeFalse();
        response.AiAssistantEnabled.Should().BeFalse();
        response.GlossaryEnabled.Should().BeFalse();
        response.AllowGlossary.Should().BeFalse();
        response.FeaturesJson.Should().Be("{\"custom\":true}");
    }

    [Fact]
    public void DedicatedGpu_ComesFromItsColumnNotFromTheTierString()
    {
        // A non-Enterprise tier that has the column set: the old Tier == "Enterprise" comparison
        // reported false here, and reported true for any Enterprise plan with the column off.
        var response = ActiveSubscription().ToFeatureAccessResponse(NonDefaultPlan());

        response.DedicatedGpu.Should().BeTrue();
    }

    [Fact]
    public void MaxLanguages_TracksThePlanColumnRatherThanAConstant()
    {
        var plan = NonDefaultPlan();
        plan.MaxLanguages = SubscriptionConstants.PlanDefaults.MaxLanguagesCeiling;

        var response = ActiveSubscription().ToFeatureAccessResponse(plan);

        response.MaxLanguages.Should().Be(SubscriptionConstants.PlanDefaults.MaxLanguagesCeiling);
    }

    [Fact]
    public void AllowAcl_MirrorsAiAssistantEnabled_BecauseNoAclColumnExists()
    {
        var plan = NonDefaultPlan();
        plan.AiAssistantEnabled = true;

        var response = ActiveSubscription().ToFeatureAccessResponse(plan);

        response.AllowAcl.Should().BeTrue();
    }

    [Fact]
    public void UnresolvablePlan_DeniesFeaturesInsteadOfGrantingThemAll()
    {
        var response = ActiveSubscription().ToFeatureAccessResponse(null);

        response.PlanTier.Should().Be(SubscriptionConstants.Tiers.NoActivePlan);
        response.MaxParticipants.Should().Be(SubscriptionConstants.PlanDefaults.MaxParticipants);
        response.MaxLanguages.Should().Be(SubscriptionConstants.PlanDefaults.MaxLanguages);
        response.VoiceCloneEnabled.Should().BeFalse();
        response.AiAssistantEnabled.Should().BeFalse();
        response.GlossaryEnabled.Should().BeFalse();
        response.DedicatedGpu.Should().BeFalse();
        response.AllowGlossary.Should().BeFalse();
        response.AllowAcl.Should().BeFalse();
        response.FeaturesJson.Should().Be(SubscriptionConstants.FeatureAccess.EmptyFeaturesJson);
    }

    [Fact]
    public void ExpiredSubscription_IsNotReportedActive_EvenWithAFullyFeaturedPlan()
    {
        var subscription = ActiveSubscription();
        subscription.CurrentPeriodEnd = DateTime.UtcNow.AddDays(-1);

        var response = subscription.ToFeatureAccessResponse(NonDefaultPlan());

        response.HasActiveSubscription.Should().BeFalse();
    }

    /// <summary>
    /// WT-262 item 3: the seeded Enterprise plan buys the platform ceiling. These were three
    /// independently maintained numbers (2, 3, 3) and drifting them apart is what let the mapper
    /// report a limit no plan actually carried.
    /// </summary>
    [Fact]
    public void EnterpriseBaseline_TracksThePlatformCeiling_AndProductionsValueOfThree()
    {
        SubscriptionConstants.EnterpriseBaseline.MaxLanguages
            .Should().Be(SubscriptionConstants.PlanDefaults.MaxLanguagesCeiling);
        SubscriptionConstants.EnterpriseBaseline.MaxLanguages.Should().Be(3);
    }

    /// <summary>The column default in subscription.plans is 2; the C# initializer must not drift.</summary>
    [Fact]
    public void PlanDefault_MatchesTheSqlColumnDefault()
    {
        SubscriptionConstants.PlanDefaults.MaxLanguages.Should().Be(2);
        new Plan().MaxLanguages.Should().Be(SubscriptionConstants.PlanDefaults.MaxLanguages);
    }
}
