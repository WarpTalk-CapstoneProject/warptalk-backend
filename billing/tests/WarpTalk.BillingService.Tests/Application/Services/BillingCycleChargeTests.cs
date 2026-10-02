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
        OveragePricePerCredit = currency == "usd" ? 0.0002m : 4m
    };

    private static Subscription Subscription(Plan plan) => new() { Id = Guid.NewGuid(), Plan = plan, PlanId = plan.Id };

    [Fact]
    public void Contract_Price_Is_Usd_Even_On_A_Vnd_Plan()
    {
        var plan = Plan("vnd");
        var sub = Subscription(plan);
        sub.ContractPriceUsd = 72m;

        var charge = BillingCycleCharge.Resolve(sub, plan).Charge!;

        charge.Currency.Should().Be(PaymentConstants.Currencies.UsdAccounting);
        charge.BasePrice.Should().Be(72m);
    }

    [Fact]
    public void Plan_Price_Keeps_The_Plan_Currency_And_Its_Spelling()
    {
        var plan = Plan("usd");
        var sub = Subscription(plan);
        sub.ContractPriceUsd = 80m;

        BillingCycleCharge.Resolve(sub, plan).Charge!.Currency.Should().Be("usd");
        BillingCycleCharge.Resolve(Subscription(Plan("vnd")), Plan("vnd")).Charge!.Currency.Should().Be("vnd");
    }

    [Fact]
    public void Usd_Overage_Override_With_Usd_Contract_Price_Sums_On_A_Vnd_Plan()
    {
        var plan = Plan("vnd");
        var sub = Subscription(plan);
        sub.ContractPriceUsd = 72m;
        sub.OveragePricePerCreditOverride = 0.0002m;
        sub.OverageCreditsThisCycle = 5_000;

        var charge = BillingCycleCharge.Resolve(sub, plan).Charge!;

        charge.OveragePricePerCredit.Should().Be(0.0002m);
        charge.OverageAmount.Should().Be(1m);
        charge.Subtotal.Should().Be(73m);
    }

    [Theory]
    [InlineData(true, false)]   // USD contract price + VND plan overage rate
    [InlineData(false, true)]   // VND plan price + USD overage override
    public void Mixed_Currencies_With_Used_Overage_Are_Refused(bool contractPrice, bool overageOverride)
    {
        var plan = Plan("vnd");
        var sub = Subscription(plan);
        if (contractPrice) sub.ContractPriceUsd = 72m;
        if (overageOverride) sub.OveragePricePerCreditOverride = 0.0002m;
        sub.OverageCreditsThisCycle = 1;

        var resolution = BillingCycleCharge.Resolve(sub, plan);

        resolution.Charge.Should().BeNull();
        resolution.CurrencyMismatch.Should().Contain("USD").And.Contain("vnd");
    }

    [Fact]
    public void Currency_Comparison_Ignores_Case()
    {
        var plan = Plan("usd");
        var sub = Subscription(plan);
        sub.OveragePricePerCreditOverride = 0.0003m;   // "USD" term against a "usd" plan
        sub.OverageCreditsThisCycle = 10_000;

        var charge = BillingCycleCharge.Resolve(sub, plan).Charge!;

        charge.Currency.Should().Be("usd");
        charge.Subtotal.Should().Be(52m);
    }
}
