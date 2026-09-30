using System;
using FluentAssertions;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Services;
using Xunit;

namespace WarpTalk.BillingService.Tests.Domain.Services;

/// <summary>
/// WT-878 (bug B): the rollover cap applies to the plan's leftover credits only. Purchased credits
/// (top-ups, credit packs) carry over whole, the same rule CreditFreezeService applies at expiry.
/// </summary>
public class SubscriptionDomainServiceRenewalTests
{
    private const int Cap = 700_000;
    private const int PerCycle = 700_000;

    private readonly SubscriptionDomainService _service = new();

    private static Subscription WithBalance(int balance) => new()
    {
        Plan = new Plan { CreditsPerCycle = PerCycle, RolloverCapCredits = Cap },
        CreditsRemaining = balance,
    };

    [Fact]
    public void Purchased_credits_are_not_capped_when_the_plan_leftover_fits_under_the_cap()
    {
        // 900k left, 300k of it bought: the plan leftover is 600k, under the 700k cap.
        // The old rule capped the whole 900k to 700k and 200k purchased credits vanished.
        var subscription = WithBalance(900_000);

        var outcome = _service.RenewCycle(subscription, purchasedCredits: 300_000);

        outcome.PurchasedCarried.Should().Be(300_000);
        outcome.PlanCarried.Should().Be(600_000);
        outcome.Forfeited.Should().Be(0);
        subscription.CreditsRemaining.Should().Be(900_000 + PerCycle);
    }

    [Fact]
    public void Only_the_plan_leftover_above_the_cap_is_forfeited()
    {
        // 1.2M left, 300k bought: plan leftover 900k, capped to 700k → 200k forfeited, 300k kept whole.
        var subscription = WithBalance(1_200_000);

        var outcome = _service.RenewCycle(subscription, purchasedCredits: 300_000);

        outcome.BalanceBefore.Should().Be(1_200_000);
        outcome.PurchasedCarried.Should().Be(300_000);
        outcome.PlanCarried.Should().Be(Cap);
        outcome.Forfeited.Should().Be(200_000);
        outcome.BalanceAfterForfeit.Should().Be(1_000_000);
        outcome.Granted.Should().Be(PerCycle);
        subscription.CreditsRemaining.Should().Be(300_000 + Cap + PerCycle);
    }

    [Fact]
    public void Purchases_larger_than_the_balance_protect_only_the_balance()
    {
        // "Spent last": more was bought than remains, so everything left is purchased.
        var subscription = WithBalance(400_000);

        var outcome = _service.RenewCycle(subscription, purchasedCredits: 1_000_000);

        outcome.PurchasedCarried.Should().Be(400_000);
        outcome.PlanCarried.Should().Be(0);
        outcome.Forfeited.Should().Be(0);
        subscription.CreditsRemaining.Should().Be(400_000 + PerCycle);
    }

    [Fact]
    public void A_balance_under_the_cap_with_no_purchases_forfeits_nothing()
    {
        var subscription = WithBalance(500_000);

        var outcome = _service.RenewCycle(subscription);

        outcome.Forfeited.Should().Be(0);
        subscription.CreditsRemaining.Should().Be(500_000 + PerCycle);
    }

    [Fact]
    public void Without_purchases_the_old_cap_rule_is_unchanged()
    {
        var subscription = WithBalance(900_000);

        var outcome = _service.RenewCycle(subscription);

        outcome.Forfeited.Should().Be(200_000);
        subscription.CreditsRemaining.Should().Be(Cap + PerCycle);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(300_000L)]
    public void A_negative_balance_behaves_exactly_as_before(long purchased)
    {
        // Overage debt was billed on the closing invoice; nothing carries, nothing is "forfeited".
        var subscription = WithBalance(-50_000);
        subscription.OverageCreditsThisCycle = 50_000;
        subscription.ServiceState = SubscriptionConstants.ServiceStates.InOverage;

        var outcome = _service.RenewCycle(subscription, purchased);

        outcome.Carried.Should().Be(0);
        outcome.Forfeited.Should().Be(0);
        subscription.CreditsRemaining.Should().Be(PerCycle);
        subscription.OverageCreditsThisCycle.Should().Be(0);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
    }
}
