using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Stripe;
using Stripe.Checkout;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.PlatformSettings;
using Xunit;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

/// <summary>
/// VAT on Stripe checkout (owner decision, 3 Oct 2026): prices are entered without VAT and one
/// platform-wide rate is added on top, as an exclusive Stripe TaxRate on every line.
/// </summary>
[Collection("StripeVatTaxRates cache")]
public class StripeVatTests
{
    private static Mock<IPlatformSettings> Settings(decimal percent)
    {
        var settings = new Mock<IPlatformSettings>();
        settings.Setup(s => s.GetDecimalAsync(PlatformSettingsCatalog.VatPercent, It.IsAny<decimal?>(), It.IsAny<SettingContext>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<decimal>(percent));
        return settings;
    }

    private static TaxRate Rate(string id, decimal percent, bool ours, bool inclusive = false) => new()
    {
        Id = id,
        Percentage = percent,
        Inclusive = inclusive,
        Metadata = ours ? new Dictionary<string, string> { [StripeVatTaxRates.MetadataKey] = "1" } : new Dictionary<string, string>(),
    };

    [Fact]
    public async Task Creates_an_exclusive_VAT_rate_when_none_of_ours_exists()
    {
        StripeVatTaxRates.ResetCacheForTests();
        TaxRateCreateOptions? created = null;
        var sdk = new Mock<IStripeSdkClient>();
        sdk.Setup(c => c.ListTaxRatesAsync(It.IsAny<TaxRateListOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeList<TaxRate> { Data = new List<TaxRate> { Rate("txr_manual", 10m, ours: false) } });
        sdk.Setup(c => c.CreateTaxRateAsync(It.IsAny<TaxRateCreateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<TaxRateCreateOptions, CancellationToken>((o, _) => created = o)
            .ReturnsAsync(new TaxRate { Id = "txr_new" });

        var id = await new StripeVatTaxRates(sdk.Object, Settings(10m).Object).ResolveTaxRateIdAsync();

        id.Should().Be("txr_new");
        created!.Percentage.Should().Be(10m);
        created.Inclusive.Should().BeFalse("prices are entered without VAT");
        created.Metadata[StripeVatTaxRates.MetadataKey].Should().Be("1");
    }

    [Fact]
    public async Task Reuses_its_own_rate_for_the_same_percentage_and_caches_it()
    {
        StripeVatTaxRates.ResetCacheForTests();
        var sdk = new Mock<IStripeSdkClient>();
        sdk.Setup(c => c.ListTaxRatesAsync(It.IsAny<TaxRateListOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeList<TaxRate> { Data = new List<TaxRate> { Rate("txr_8", 8m, ours: true), Rate("txr_ours", 11m, ours: true) } });
        var resolver = new StripeVatTaxRates(sdk.Object, Settings(11m).Object);

        (await resolver.ResolveTaxRateIdAsync()).Should().Be("txr_ours");
        (await resolver.ResolveTaxRateIdAsync()).Should().Be("txr_ours");

        sdk.Verify(c => c.ListTaxRatesAsync(It.IsAny<TaxRateListOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        sdk.Verify(c => c.CreateTaxRateAsync(It.IsAny<TaxRateCreateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Zero_percent_turns_VAT_off()
    {
        var sdk = new Mock<IStripeSdkClient>(MockBehavior.Strict);
        (await new StripeVatTaxRates(sdk.Object, Settings(0m).Object).ResolveTaxRateIdAsync()).Should().BeNull();
    }

    private static (StripePaymentService Service, Func<SessionCreateOptions?> Captured) Checkout(string? taxRateId)
    {
        SessionCreateOptions? captured = null;
        var sdk = new Mock<IStripeSdkClient>();
        sdk.Setup(c => c.CreateCheckoutSessionAsync(It.IsAny<SessionCreateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<SessionCreateOptions, CancellationToken>((options, _) => captured = options)
            .ReturnsAsync(new Session { Id = "cs_test_1", Url = "https://checkout.stripe.test/c/cs_test_1" });
        var vat = new Mock<IStripeVatTaxRates>();
        vat.Setup(v => v.ResolveTaxRateIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(taxRateId);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PaymentConstants.StripeConfigKeys.SecretKey] = "configured-for-test",
            [PaymentConstants.StripeConfigKeys.SuccessUrl] = "https://app.example.test/ok",
            [PaymentConstants.StripeConfigKeys.CancelUrl] = "https://app.example.test/cancel",
        }).Build();
        return (new StripePaymentService(configuration, sdk.Object, vat.Object), () => captured);
    }

    [Theory]
    [InlineData(PaymentConstants.PaymentTypes.Subscription)]
    [InlineData(PaymentConstants.PaymentTypes.CreditPack)]
    public async Task Every_sold_line_carries_the_VAT_rate(string paymentType)
    {
        var (service, captured) = Checkout("txr_vat");

        var result = await service.CreateCheckoutSessionAsync(
            new CreateCheckoutSessionRequest(Guid.NewGuid(), Guid.NewGuid(), 500m, "usd", paymentType, "pro", "monthly"));

        result.IsSuccess.Should().BeTrue();
        captured()!.LineItems.Should().OnlyContain(line => line.TaxRates != null && line.TaxRates.Contains("txr_vat"));
    }

    [Fact]
    public async Task An_issued_invoice_is_paid_at_its_own_total()
    {
        var (service, captured) = Checkout("txr_vat");

        await service.CreateCheckoutSessionAsync(
            new CreateCheckoutSessionRequest(Guid.NewGuid(), Guid.NewGuid(), 500m, "usd", PaymentConstants.PaymentTypes.InvoicePayment, "", ""));

        captured()!.LineItems.Should().OnlyContain(line => line.TaxRates == null);
    }

    [Fact]
    public async Task The_webhook_reports_the_net_amount_and_the_VAT_apart()
    {
        StripePaymentEventRequest? captured = null;
        var app = new Mock<IPaymentAppService>();
        app.Setup(s => s.ProcessPaymentEventAsync(It.IsAny<StripePaymentEventRequest>()))
            .Callback<StripePaymentEventRequest>(r => captured = r)
            .ReturnsAsync(Result.Success());
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PaymentConstants.StripeConfigKeys.WebhookSecret] = string.Empty,
        }).Build();
        var service = new StripeWebhookService(app.Object, configuration, environment.Object,
            Mock.Of<ILogger<StripeWebhookService>>(), new Stripe.SubscriptionService());

        var json = $$"""
        {
          "id": "evt_paid", "object": "event", "type": "{{PaymentConstants.StripeEvents.CheckoutSessionCompleted}}",
          "data": { "object": {
            "id": "cs_test_vat", "object": "checkout.session", "currency": "usd",
            "amount_subtotal": 50000, "amount_total": 55000,
            "total_details": { "amount_discount": 0, "amount_shipping": 0, "amount_tax": 5000 },
            "payment_status": "paid", "status": "complete",
            "metadata": { "{{PaymentConstants.StripeMetadata.PaymentType}}": "{{PaymentConstants.PaymentTypes.Subscription}}" }
          } }
        }
        """;

        (await service.HandleWebhookAsync(json, string.Empty, CancellationToken.None)).IsSuccess.Should().BeTrue();

        captured!.Amount.Should().Be(500m, "the price checks and revenue compare against the net amount");
        captured.TaxAmount.Should().Be(50m);
    }

    [Fact]
    public void The_payment_row_keeps_net_tax_and_total()
    {
        var payment = PaymentMapper.CreateStripePayment(new StripePaymentCreationRequest(
            Guid.NewGuid(), Guid.NewGuid(), 500m, "usd", "pi_1", PaymentConstants.PaymentStatuses.Paid, null, TaxAmount: 50m));

        payment.Amount.Should().Be(500m);
        payment.TaxAmount.Should().Be(50m);
        payment.TotalAmount.Should().Be(550m);
    }
}

[CollectionDefinition("StripeVatTaxRates cache", DisableParallelization = true)]
public class StripeVatTaxRatesCacheCollection
{
}
