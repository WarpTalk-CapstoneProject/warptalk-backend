using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe.Checkout;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Services;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

public class StripePaymentServiceTests
{
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
