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
        var workspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var status = SubscriptionConstants.SubscriptionStatuses.Active;
        var serviceState = SubscriptionConstants.ServiceStates.Healthy;
        var contractPrice = 1900000m;
        var overageCapOverride = 105000;
        var billingContactEmail = "finance@company.com";
        var ownerEmailDomain = "company.com";

        // Act
        var subscription = new Subscription
        {
            Id = id,
            UserId = userId,
            WorkspaceId = workspaceId,
            PlanId = planId,
            Status = status,
            ServiceState = serviceState,
            CreditsRemaining = 700000,
            ContractPriceVnd = contractPrice,
            OverageCapCreditsOverride = overageCapOverride,
            BillingContactEmail = billingContactEmail,
            OwnerEmailDomain = ownerEmailDomain,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        // Assert
        subscription.Id.Should().Be(id);
        subscription.UserId.Should().Be(userId);
        subscription.WorkspaceId.Should().Be(workspaceId);
        subscription.PlanId.Should().Be(planId);
        subscription.Status.Should().Be(status);
        subscription.ServiceState.Should().Be(serviceState);
        subscription.CreditsRemaining.Should().Be(700000);
        subscription.ContractPriceVnd.Should().Be(contractPrice);
        subscription.OverageCapCreditsOverride.Should().Be(overageCapOverride);
        subscription.BillingContactEmail.Should().Be(billingContactEmail);
        subscription.OwnerEmailDomain.Should().Be(ownerEmailDomain);
        subscription.IsActive.Should().BeTrue();
    }

    [Fact]
    public void ConsumeCredits_ShouldDeductCredits_WhenSufficientBalance()
    {
        var subscription = new Subscription { CreditsRemaining = 100 };
        var domainService = new WarpTalk.BillingService.Domain.Services.SubscriptionDomainService();
        var result = domainService.ConsumeCredits(subscription, 40);

        result.Should().BeTrue();
        subscription.CreditsRemaining.Should().Be(60);
        subscription.CreditsUsedThisCycle.Should().Be(40);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
    }

    [Fact]
    public void ConsumeCredits_ShouldTransitionToLowBalance_WhenUnderThreshold()
    {
        var plan = new Plan { LowBalanceThresholdCredits = 50 };
        var subscription = new Subscription { CreditsRemaining = 100, Plan = plan };
        var domainService = new WarpTalk.BillingService.Domain.Services.SubscriptionDomainService();
        
        var result = domainService.ConsumeCredits(subscription, 60);

        result.Should().BeTrue();
        subscription.CreditsRemaining.Should().Be(40);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.LowBalance);
    }

    [Fact]
    public void ConsumeCredits_ShouldTransitionToOverage_WhenBalanceBelowZero()
    {
        var plan = new Plan { OverageCapCredits = 100 };
        var subscription = new Subscription { CreditsRemaining = 20, Plan = plan };
        var domainService = new WarpTalk.BillingService.Domain.Services.SubscriptionDomainService();

        var result = domainService.ConsumeCredits(subscription, 50);

        result.Should().BeTrue();
        subscription.CreditsRemaining.Should().Be(-30);
        subscription.OverageCreditsThisCycle.Should().Be(30);
        subscription.OverageStartedAt.Should().NotBeNull();
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.InOverage);
    }

    [Fact]
    public void ConsumeCredits_ShouldSuspend_WhenExceedingOverageCap()
    {
        var plan = new Plan { OverageCapCredits = 100 };
        var subscription = new Subscription { CreditsRemaining = -80, Plan = plan };
        var domainService = new WarpTalk.BillingService.Domain.Services.SubscriptionDomainService();

        var result = domainService.ConsumeCredits(subscription, 50);

        result.Should().BeFalse();
        subscription.CreditsRemaining.Should().Be(-80); // Unchanged
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.OverageCap);
    }

    [Fact]
    public void RenewCycle_ShouldRolloverCredits_UpToCap()
    {
        var plan = new Plan { CreditsPerCycle = 1000, RolloverCapCredits = 200 };
        var subscription = new Subscription { CreditsRemaining = 500, Plan = plan };
        var domainService = new WarpTalk.BillingService.Domain.Services.SubscriptionDomainService();

        domainService.RenewCycle(subscription);

        // Should only carry over 200 out of 500
        subscription.CreditsRemaining.Should().Be(1200); 
        subscription.CreditsUsedThisCycle.Should().Be(0);
    }

    [Fact]
    public void RenewCycle_ShouldNotRolloverNegativeCredits()
    {
        var plan = new Plan { CreditsPerCycle = 1000, RolloverCapCredits = 200 };
        var subscription = new Subscription { 
            CreditsRemaining = -50, 
            Plan = plan,
            ServiceState = SubscriptionConstants.ServiceStates.InOverage 
        };
        var domainService = new WarpTalk.BillingService.Domain.Services.SubscriptionDomainService();

        domainService.RenewCycle(subscription);

        // Overage debt is not carried over via rollover, it resets (debt is handled via invoice)
        // Note: Master plan says "Mang theo số dương hoặc reset về 0". 
        // Here Math.Max(CreditsRemaining, 0) handles it.
        subscription.CreditsRemaining.Should().Be(1000); 
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscription.OverageStartedAt.Should().BeNull();
    }
}
