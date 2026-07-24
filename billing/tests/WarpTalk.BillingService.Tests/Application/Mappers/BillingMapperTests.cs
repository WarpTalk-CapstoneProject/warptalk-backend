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
}
