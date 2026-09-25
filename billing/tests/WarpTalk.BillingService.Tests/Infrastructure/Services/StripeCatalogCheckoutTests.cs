using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe.Checkout;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Services;
using Xunit;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

/// <summary>G11: the Stripe session a catalog checkout creates.</summary>
public class StripeCatalogCheckoutTests
{
    private static (StripePaymentService Service, Func<SessionCreateOptions?> Captured) Build()
    {
        SessionCreateOptions? captured = null;
        var sdk = new Mock<IStripeSdkClient>();
        sdk.Setup(c => c.CreateCheckoutSessionAsync(It.IsAny<SessionCreateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<SessionCreateOptions, CancellationToken>((options, _) => captured = options)
            .ReturnsAsync(new Session { Id = "cs_test_1", Url = "https://checkout.stripe.test/c/cs_test_1" });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PaymentConstants.StripeConfigKeys.SecretKey] = "configured-for-test",
            [PaymentConstants.StripeConfigKeys.SuccessUrl] = "https://app.example.test/ok",
            [PaymentConstants.StripeConfigKeys.CancelUrl] = "https://app.example.test/cancel",
        }).Build();
        return (new StripePaymentService(configuration, sdk.Object), () => captured);
    }

    private static CreateCheckoutSessionRequest Request(string type) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, "vnd", type, "", "monthly");

    [Fact]
    public async Task A_synced_pack_is_sold_by_its_Stripe_price_with_the_promotion_code()
    {
        var (service, captured) = Build();
        var extras = new CheckoutExtras(
            new CatalogCheckoutLine("Starter", "prod_1", "price_1", 99_000m, "vnd", 1, null),
            null, "promo_1",
            new Dictionary<string, string> { [PackageCatalogConstants.StripeMetadata.PackageId] = "p" });

        var result = await service.CreateCatalogCheckoutSessionAsync(Request(PaymentConstants.PaymentTypes.CreditPack), extras);

        result.IsSuccess.Should().BeTrue();
        var options = captured()!;
        options.Mode.Should().Be(PaymentConstants.StripeModes.Payment);
        options.LineItems.Should().ContainSingle(item => item.Price == "price_1" && item.Quantity == 1);
        options.Discounts.Should().ContainSingle(discount => discount.PromotionCode == "promo_1" && discount.Coupon == null);
        options.PaymentIntentData.Metadata.Should().ContainKey(PackageCatalogConstants.StripeMetadata.PackageId);
    }

    [Fact]
    public async Task An_unsynced_addon_is_a_recurring_inline_price_whose_subscription_says_it_is_an_addon()
    {
        var (service, captured) = Build();
        var extras = new CheckoutExtras(
            new CatalogCheckoutLine("Extra participants", null, null, 12.5m, "usd", 3, PaymentConstants.PriceIntervals.Month),
            "co_1", null, new Dictionary<string, string>());

        await service.CreateCatalogCheckoutSessionAsync(Request(PaymentConstants.PaymentTypes.AddOn), extras);

        var options = captured()!;
        options.Mode.Should().Be(PaymentConstants.StripeModes.Subscription);
        var line = options.LineItems.Should().ContainSingle().Subject;
        line.Quantity.Should().Be(3);
        line.PriceData.UnitAmount.Should().Be(1250);
        line.PriceData.Recurring.Interval.Should().Be(PaymentConstants.PriceIntervals.Month);
        options.Discounts.Should().ContainSingle(discount => discount.Coupon == "co_1");
        options.SubscriptionData.Metadata[PaymentConstants.StripeMetadata.PaymentType].Should().Be(PaymentConstants.PaymentTypes.AddOn);
    }

    [Theory]
    [InlineData(99_000, "vnd", 99_000)]
    [InlineData(4.99, "usd", 499)]
    [InlineData(0.105, "usd", 11)]
    public void Minor_units_follow_the_currency(decimal amount, string currency, long expected) =>
        StripePaymentService.ToMinorUnits(amount, currency).Should().Be(expected);

    [Fact]
    public void Stripe_errors_never_carry_an_api_key() =>
        StripeCatalogSyncService.Redact(new InvalidOperationException("Invalid API Key provided: sk_test_51AbC****wxyz"))
            .Should().NotContain("sk_test_").And.Contain("[redacted key]");
}
