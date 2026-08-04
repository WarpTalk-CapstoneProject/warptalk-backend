using System;
using FluentAssertions;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Services;
using Xunit;

namespace WarpTalk.BillingService.Tests.Domain.Services;

public class SubscriptionDomainServiceTests
{
    private readonly SubscriptionDomainService _service = new();

    [Fact]
    public void ConsumeCredits_Should_Not_Reset_OverageCreditsThisCycle_When_Balance_Goes_Positive()
    {
        // 1. Arrange
        var plan = new Plan
        {
            OverageCapCredits = 100
        };
        var subscription = new Subscription
        {
            Plan = plan,
            CreditsRemaining = -50,
            OverageCreditsThisCycle = 50,
            OverageStartedAt = DateTime.UtcNow.AddDays(-1)
        };

        // Mid-cycle, for some reason, we add credits (e.g. they paid or top up)
        // Wait, ConsumeCredits doesn't support negative amount to simulate add_credits.
        // But what if they add credits directly?
        subscription.CreditsRemaining = 100;
        
        // 2. Act
        // Then they consume 10 credits
        var result = _service.ConsumeCredits(subscription, 10);

        // 3. Assert
        result.Should().BeTrue();
        subscription.CreditsRemaining.Should().Be(90);
        // It should NOT reset the overage!
        subscription.OverageCreditsThisCycle.Should().Be(50);
    }

    [Fact]
    public void RenewCycle_InOverage_Should_Not_Carry_Negative_Balance()
    {
        var plan = new Plan { CreditsPerCycle = 1000, RolloverCapCredits = 200 };
        var subscription = new Subscription
        {
            Plan = plan,
            CreditsRemaining = -50,
            OverageCreditsThisCycle = 50
        };

        _service.RenewCycle(subscription);

        // Carry should be 0 (since it's negative). New balance should be just creditsPerCycle (1000).
        // The negative balance (-50) was already billed via OverageCreditsThisCycle = 50 in the invoice.
        subscription.CreditsRemaining.Should().Be(1000);
        subscription.OverageCreditsThisCycle.Should().Be(0);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
    }

    [Fact]
    public void RenewCycle_WithSurplus_Should_Cap_At_RolloverCap()
    {
        var plan = new Plan { CreditsPerCycle = 1000, RolloverCapCredits = 200 };
        var subscription = new Subscription
        {
            Plan = plan,
            CreditsRemaining = 500,
            OverageCreditsThisCycle = 0
        };

        _service.RenewCycle(subscription);

        // Carry should be min(500, 200) = 200. New balance should be 200 + 1000 = 1200.
        subscription.CreditsRemaining.Should().Be(1200);
    }
}
