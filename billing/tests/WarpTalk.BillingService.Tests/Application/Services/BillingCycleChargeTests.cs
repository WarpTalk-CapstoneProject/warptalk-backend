using FluentAssertions;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class BillingCycleChargeTests
{
    private static Plan Plan(string currency) => new()
    {
        Id = Guid.NewGuid(),
        Price = currency == "usd" ? 49m : 1_000_000m,
        Currency = currency,
        OveragePricePerCredit = currency == "usd" ? 0.02m : 4m
    };

    private static Subscription Subscription(Plan plan) => new() { Id = Guid.NewGuid(), Plan = plan, PlanId = plan.Id };

    [Fact]
    public void Contract_Price_Is_Vnd_Even_On_A_Usd_Plan()
    {
        var plan = Plan("usd");
        var sub = Subscription(plan);
        sub.ContractPriceVnd = 1_900_000m;

        var charge = BillingCycleCharge.Resolve(sub, plan).Charge!;

        charge.Currency.Should().Be(PaymentConstants.Currencies.VndAccounting);
        charge.BasePrice.Should().Be(1_900_000m);
    }

    [Fact]
    public void Plan_Price_Keeps_The_Plan_Currency_And_Its_Spelling()
    {
        var plan = Plan("vnd");
        var sub = Subscription(plan);
        sub.ContractPriceVnd = 2_000_000m;

        BillingCycleCharge.Resolve(sub, plan).Charge!.Currency.Should().Be("vnd");
        BillingCycleCharge.Resolve(Subscription(Plan("usd")), Plan("usd")).Charge!.Currency.Should().Be("usd");
    }

    [Fact]
    public void Vnd_Overage_Override_With_Vnd_Contract_Price_Sums_On_A_Usd_Plan()
    {
        var plan = Plan("usd");
        var sub = Subscription(plan);
        sub.ContractPriceVnd = 1_900_000m;
        sub.OveragePricePerCreditOverride = 5m;
        sub.OverageCreditsThisCycle = 200;

        var charge = BillingCycleCharge.Resolve(sub, plan).Charge!;

        charge.OveragePricePerCredit.Should().Be(5m);
        charge.OverageAmount.Should().Be(1_000m);
        charge.Subtotal.Should().Be(1_901_000m);
    }

    [Theory]
    [InlineData(true, false)]   // VND contract price + USD plan overage rate
    [InlineData(false, true)]   // USD plan price + VND overage override
    public void Mixed_Currencies_With_Used_Overage_Are_Refused(bool contractPrice, bool overageOverride)
    {
        var plan = Plan("usd");
        var sub = Subscription(plan);
        if (contractPrice) sub.ContractPriceVnd = 1_900_000m;
        if (overageOverride) sub.OveragePricePerCreditOverride = 4m;
        sub.OverageCreditsThisCycle = 1;

        var resolution = BillingCycleCharge.Resolve(sub, plan);

        resolution.Charge.Should().BeNull();
        resolution.CurrencyMismatch.Should().Contain("VND").And.Contain("usd");
    }

    [Fact]
    public void Currency_Comparison_Ignores_Case()
    {
        var plan = Plan("vnd");
        var sub = Subscription(plan);
        sub.OveragePricePerCreditOverride = 6m;   // "VND" term against a "vnd" plan
        sub.OverageCreditsThisCycle = 10;

        var charge = BillingCycleCharge.Resolve(sub, plan).Charge!;

        charge.Currency.Should().Be("vnd");
        charge.Subtotal.Should().Be(1_000_060m);
    }
}
