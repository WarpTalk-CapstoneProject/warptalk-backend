using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Stripe;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

public class StripeWebhookServiceTests
{
    [Theory]
    [InlineData("vnd", 499000, 499000)]
    [InlineData("usd", 49900, 499)]
    public async Task HandleWebhookAsync_NormalizesPaymentIntentFailureAmountByCurrency(
        string currency,
        long stripeAmount,
        decimal expectedAmount)
    {
        StripePaymentEventRequest? capturedRequest = null;
        var paymentAppService = new Mock<IPaymentAppService>();
        paymentAppService
            .Setup(service => service.ProcessPaymentEventAsync(It.IsAny<StripePaymentEventRequest>()))
            .Callback<StripePaymentEventRequest>(request => capturedRequest = request)
            .ReturnsAsync(Result.Success());

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PaymentConstants.StripeConfigKeys.WebhookSecret] = string.Empty
            })
            .Build();

        var service = new StripeWebhookService(
            paymentAppService.Object,
            configuration,
            environment.Object,
            Mock.Of<ILogger<StripeWebhookService>>(),
            new Stripe.SubscriptionService());

        var result = await service.HandleWebhookAsync(
            CreatePaymentIntentFailedEventJson(currency, stripeAmount),
            signatureHeader: string.Empty,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Amount.Should().Be(expectedAmount);
        capturedRequest.Currency.Should().Be(currency);
        capturedRequest.Status.Should().Be(PaymentConstants.PaymentStatuses.Failed);
    }

    private static string CreatePaymentIntentFailedEventJson(string currency, long amount) =>
        $$"""
        {
          "id": "evt_payment_failed",
          "object": "event",
          "type": "{{PaymentConstants.StripeEvents.PaymentIntentPaymentFailed}}",
          "data": {
            "object": {
              "id": "pi_failed",
              "object": "payment_intent",
              "amount": {{amount}},
              "currency": "{{currency}}",
              "metadata": {
                "{{PaymentConstants.StripeMetadata.UserId}}": "{{Guid.NewGuid()}}",
                "{{PaymentConstants.StripeMetadata.WorkspaceId}}": "{{Guid.NewGuid()}}",
                "{{PaymentConstants.StripeMetadata.PaymentType}}": "{{PaymentConstants.PaymentTypes.CreditTopUp}}"
              },
              "last_payment_error": {
                "message": "Card declined"
              }
            }
          }
        }
        """;
}
