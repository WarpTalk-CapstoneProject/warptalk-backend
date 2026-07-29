using WarpTalk.BillingService.Domain.Constants;
using System;
using FluentAssertions;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;


namespace WarpTalk.BillingService.Tests.Domain.Entities;

public class SubscriptionTests
{
    [Fact]
    public void CreateSubscription_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var status = SubscriptionConstants.SubscriptionStatuses.Active;

        // Act
        var subscription = new Subscription
        {
            Id = id,
            UserId = userId,
            PlanId = planId,
            Status = status,
            CreditsRemaining = 1000,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        // Assert
        subscription.Id.Should().Be(id);
        subscription.UserId.Should().Be(userId);
        subscription.PlanId.Should().Be(planId);
        subscription.Status.Should().Be(status);
        subscription.CreditsRemaining.Should().Be(1000);
        subscription.IsActive.Should().BeTrue();
    }
}
