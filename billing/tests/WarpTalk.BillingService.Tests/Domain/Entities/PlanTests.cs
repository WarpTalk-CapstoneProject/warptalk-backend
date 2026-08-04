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
        var name = "Enterprise";
        var slug = "enterprise";
        var tier = "enterprise";
        var price = 1900000m;
        var currency = "VND";
        var billingCycle = "Monthly";
        var creditsPerCycle = 700000;
        var overageCapCredits = 105000;
        var rolloverCapCredits = 700000;
        var lowBalanceThresholdCredits = 140000;
        var invoiceTermsDays = 15;
        var invoiceGraceHours = 360;

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
            OverageCapCredits = overageCapCredits,
            RolloverCapCredits = rolloverCapCredits,
            LowBalanceThresholdCredits = lowBalanceThresholdCredits,
            InvoiceTermsDays = invoiceTermsDays,
            InvoiceGraceHours = invoiceGraceHours,
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
        plan.OverageCapCredits.Should().Be(overageCapCredits);
        plan.RolloverCapCredits.Should().Be(rolloverCapCredits);
        plan.LowBalanceThresholdCredits.Should().Be(lowBalanceThresholdCredits);
        plan.InvoiceTermsDays.Should().Be(invoiceTermsDays);
        plan.InvoiceGraceHours.Should().Be(invoiceGraceHours);
        plan.IsActive.Should().BeTrue();
    }
}
