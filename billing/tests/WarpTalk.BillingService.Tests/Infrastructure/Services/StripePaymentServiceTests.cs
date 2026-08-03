using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe.Checkout;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Services;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

public class StripePaymentServiceTests
{
    [Fact]
    public async Task PlaceholderSecret_Should_Fail_CheckoutSession_Creation()
    {
        var stripeClient = new Mock<IStripeSdkClient>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PaymentConstants.StripeConfigKeys.SecretKey] = PaymentConstants.StripePlaceholders.SecretKeyPlaceholder,
                [PaymentConstants.StripeConfigKeys.SuccessUrl] = "https://app.example.test/billing/success?session_id={CHECKOUT_SESSION_ID}",
                [PaymentConstants.StripeConfigKeys.CancelUrl] = "https://app.example.test/billing/cancel"
            })
            .Build();
        var service = new StripePaymentService(configuration, stripeClient.Object);
        var request = new CreateCheckoutSessionRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            26300m,
            PaymentConstants.Currencies.Vnd,
            PaymentConstants.PaymentTypes.InvoicePayment,
            "enterprise",
            "monthly");

        var checkoutResult = await service.CreateCheckoutSessionAsync(request);
        checkoutResult.IsSuccess.Should().BeFalse();
        checkoutResult.Error.Should().Be(PaymentConstants.StripeErrorMessages.SecretKeyNotConfigured);
        stripeClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetPaymentStatusAsync_Should_Read_Session_Status_From_StripeSdkClient()
    {
        var stripeClient = new Mock<IStripeSdkClient>();
        stripeClient
            .Setup(c => c.GetCheckoutSessionAsync("cs_test_paid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Session
            {
                Id = "cs_test_paid",
                PaymentStatus = PaymentConstants.StripeStatuses.Paid
            });

        var service = new StripePaymentService(
            new ConfigurationBuilder().Build(),
            stripeClient.Object);

        var result = await service.GetPaymentStatusAsync("cs_test_paid");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(PaymentConstants.PaymentStatuses.Paid);
        stripeClient.Verify(c => c.GetCheckoutSessionAsync("cs_test_paid", It.IsAny<CancellationToken>()), Times.Once);
    }
}
