using WarpTalk.BillingService.Domain.Constants;
using System;
using Xunit;
using FluentAssertions;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;


namespace WarpTalk.BillingService.Tests.Application.Mappers;

public class BillingMapperTests
{
    [Fact]
    public void ToEntity_Should_Create_Pending_Subscription_With_Correct_Initial_Dates()
    {
        var request = new SubscriptionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var plan = new Plan { Id = request.PlanId, BillingCycle = "monthly", CreditsPerCycle = 1000 };
        var beforeTime = DateTime.UtcNow;

        var subscription = request.ToEntity(plan);

        subscription.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Pending);
        subscription.IsActive.Should().BeFalse();
        subscription.CreditsRemaining.Should().Be(0); // Credits are only granted upon payment
        subscription.CurrentPeriodStart.Should().BeOnOrAfter(beforeTime);
        subscription.CurrentPeriodEnd.Should().Be(subscription.CurrentPeriodStart); // Period hasn't started ticking
    }

    [Fact]
    public void ToTrialEntity_Should_Create_Active_Trial_Subscription()
    {
        var request = new TrialSubscriptionRequest(Guid.NewGuid(), Guid.NewGuid(), "owner@example.com");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Enterprise", Price = 0m };
        var before = DateTime.UtcNow;

        var subscription = request.ToTrialEntity(plan, "example.com");

        subscription.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
        subscription.IsActive.Should().BeTrue();
        subscription.AutoRenew.Should().BeFalse();
        subscription.OwnerEmailDomain.Should().Be("example.com");
        subscription.BillingContactEmail.Should().Be("owner@example.com");
        subscription.CreditsRemaining.Should().Be(SubscriptionConstants.TrialDefaults.Credits);
        subscription.CreditsPerCycleOverride.Should().Be(SubscriptionConstants.TrialDefaults.Credits);
        subscription.OverageCapCreditsOverride.Should().Be(SubscriptionConstants.TrialDefaults.OverageCapCredits);
        subscription.TrialEndsAt.Should().BeOnOrAfter(before.AddDays(SubscriptionConstants.TrialDefaults.DurationDays));
    }
}
