using System;
using FluentAssertions;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;

namespace WarpTalk.BillingService.Tests.Domain.Entities;

public class PlanTests
{
    [Fact]
    public void CreatePlan_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var name = "Pro Plan";
        var slug = "pro-plan";
        var tier = "Pro";
        var price = 9.99m;
        var currency = "USD";
        var billingCycle = "Monthly";
        var creditsPerCycle = 1000;

        // Act
        var plan = new Plan
        {
            Id = id,
            Name = name,
            Slug = slug,
            Tier = tier,
            Price = price,
            Currency = currency,
            BillingCycle = billingCycle,
            CreditsPerCycle = creditsPerCycle,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        // Assert
        plan.Id.Should().Be(id);
        plan.Name.Should().Be(name);
        plan.Slug.Should().Be(slug);
        plan.Tier.Should().Be(tier);
        plan.Price.Should().Be(price);
        plan.Currency.Should().Be(currency);
        plan.BillingCycle.Should().Be(billingCycle);
        plan.CreditsPerCycle.Should().Be(creditsPerCycle);
        plan.IsActive.Should().BeTrue();
    }
}
