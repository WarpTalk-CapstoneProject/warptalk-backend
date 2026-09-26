using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Services;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

/// <summary>
/// #466 — the plan's recurring Stripe Price. Created once, reused while it matches, archived (never
/// deleted) when the plan's price changes. The SDK is a mock; nothing here reaches Stripe.
/// </summary>
public class StripeRecurringGatewayTests
{
    private static WarpTalk.BillingService.Domain.Entities.Plan Plan(decimal price = 499_000m) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Startup",
        Slug = "startup",
        Price = price,
        Currency = "VND",
        BillingCycle = SubscriptionConstants.BillingCycles.Monthly,
    };

    private static Price StripePrice(string id, string product, long amount, string interval, bool active = true) => new()
    {
        Id = id,
        ProductId = product,
        Currency = "vnd",
        UnitAmount = amount,
        Active = active,
        Recurring = new PriceRecurring { Interval = interval },
    };

    private static (StripeRecurringGateway Gateway, Mock<IStripeSdkClient> Sdk) Build()
    {
        var sdk = new Mock<IStripeSdkClient>();
        sdk.Setup(s => s.CreateProductAsync(It.IsAny<ProductCreateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod_test_plan" });
        sdk.Setup(s => s.ListPricesAsync(It.IsAny<PriceListOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeList<Price> { Data = new List<Price>() });
        sdk.Setup(s => s.CreatePriceAsync(It.IsAny<PriceCreateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PriceCreateOptions o, CancellationToken _) => StripePrice("price_test_new", o.Product, o.UnitAmount!.Value, o.Recurring.Interval));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PaymentConstants.StripeConfigKeys.SecretKey] = "sk_test_placeholder_for_tests" })
            .Build();
        return (new StripeRecurringGateway(sdk.Object, configuration), sdk);
    }

    [Fact]
    public async Task The_first_auto_renew_checkout_creates_the_product_and_a_recurring_price_with_a_lookup_key()
    {
        var (gateway, sdk) = Build();
        var plan = Plan();

        var result = await gateway.EnsurePlanPriceAsync(plan, "monthly", 499_000m, "vnd");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("price_test_new");
        plan.StripeProductId.Should().Be("prod_test_plan");
        plan.StripePriceIds.Should().Contain("monthly_vnd").And.Contain("price_test_new");
        sdk.Verify(s => s.CreatePriceAsync(It.Is<PriceCreateOptions>(o =>
            o.Recurring.Interval == "month"
            && o.UnitAmount == 499_000
            && o.Currency == "vnd"
            && o.LookupKey!.StartsWith("warptalk_plan_")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_matching_stored_price_is_reused()
    {
        var (gateway, sdk) = Build();
        var plan = Plan();
        plan.StripeProductId = "prod_test_plan";
        plan.StripePriceIds = """{"monthly_vnd":"price_test_old"}""";
        sdk.Setup(s => s.GetPriceAsync("price_test_old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(StripePrice("price_test_old", "prod_test_plan", 499_000, "month"));

        var result = await gateway.EnsurePlanPriceAsync(plan, "monthly", 499_000m, "vnd");

        result.Value.Should().Be("price_test_old");
        sdk.Verify(s => s.CreatePriceAsync(It.IsAny<PriceCreateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        sdk.Verify(s => s.CreateProductAsync(It.IsAny<ProductCreateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_changed_plan_price_archives_the_old_price_and_creates_a_new_one()
    {
        var (gateway, sdk) = Build();
        var plan = Plan(price: 599_000m);
        plan.StripeProductId = "prod_test_plan";
        plan.StripePriceIds = """{"monthly_vnd":"price_test_old"}""";
        sdk.Setup(s => s.GetPriceAsync("price_test_old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(StripePrice("price_test_old", "prod_test_plan", 499_000, "month"));

        var result = await gateway.EnsurePlanPriceAsync(plan, "monthly", 599_000m, "vnd");

        result.Value.Should().Be("price_test_new");
        sdk.Verify(s => s.UpdatePriceAsync("price_test_old", It.Is<PriceUpdateOptions>(o => o.Active == false), It.IsAny<CancellationToken>()), Times.Once,
            "archived, never deleted: live subscriptions keep the price they were sold at");
        plan.StripePriceIds.Should().Contain("price_test_new").And.NotContain("price_test_old");
    }

    [Fact]
    public async Task A_price_another_checkout_just_created_is_found_by_its_lookup_key()
    {
        var (gateway, sdk) = Build();
        var plan = Plan();
        plan.StripeProductId = "prod_test_plan";
        sdk.Setup(s => s.ListPricesAsync(It.IsAny<PriceListOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeList<Price> { Data = new List<Price> { StripePrice("price_test_raced", "prod_test_plan", 499_000, "month") } });

        var result = await gateway.EnsurePlanPriceAsync(plan, "monthly", 499_000m, "vnd");

        result.Value.Should().Be("price_test_raced");
        sdk.Verify(s => s.CreatePriceAsync(It.IsAny<PriceCreateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
