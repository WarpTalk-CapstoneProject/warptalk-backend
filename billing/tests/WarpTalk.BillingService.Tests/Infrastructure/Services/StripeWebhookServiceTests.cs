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

    /// <summary>
    /// WT-370. The evidence this pins is a Stripe dashboard reading
    /// "checkout.session.completed — 200 OK — Delivered", four times over, for a workspace that
    /// never got its plan. Answering 200 on a processing failure told Stripe the event was handled
    /// and discarded ~3 days of free redeliveries — the exact mechanism that would have recovered
    /// a transient database fault on its own.
    ///
    /// If this test ever goes red, a paid subscription can be dropped in silence again.
    /// </summary>
    [Fact]
    public async Task HandleWebhookAsync_ReportsFailure_WhenProcessingFailsForARetryableReason()
    {
        var service = BuildService(Result.Failure("Database was unreachable.", ErrorCodes.InternalServerError));

        var result = await service.HandleWebhookAsync(
            CreatePaymentIntentFailedEventJson("vnd", 1_900_000),
            signatureHeader: string.Empty,
            CancellationToken.None);

        // Non-success here is what makes the controller answer non-2xx, which is what makes
        // Stripe redeliver.
        result.IsSuccess.Should().BeFalse();
    }

    /// <summary>
    /// The other half of the rule: a payload that cannot be used is acknowledged rather than
    /// redelivered for three days, because every one of those redeliveries would fail identically.
    /// It is logged at Error instead.
    /// </summary>
    [Theory]
    [InlineData(ErrorCodes.ValidationError)]
    [InlineData(ErrorCodes.BillingPlanNotFound)]
    [InlineData(ErrorCodes.NotFound)]
    public async Task HandleWebhookAsync_Acknowledges_WhenARedeliveryCouldNotHelp(string errorCode)
    {
        var service = BuildService(Result.Failure("The payload cannot be used.", errorCode));

        var result = await service.HandleWebhookAsync(
            CreatePaymentIntentFailedEventJson("vnd", 1_900_000),
            signatureHeader: string.Empty,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    private static StripeWebhookService BuildService(Result processingOutcome)
    {
        var paymentAppService = new Mock<IPaymentAppService>();
        paymentAppService
            .Setup(service => service.ProcessPaymentEventAsync(It.IsAny<StripePaymentEventRequest>()))
            .ReturnsAsync(processingOutcome);

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PaymentConstants.StripeConfigKeys.WebhookSecret] = string.Empty
            })
            .Build();

        return new StripeWebhookService(
            paymentAppService.Object,
            configuration,
            environment.Object,
            Mock.Of<ILogger<StripeWebhookService>>(),
            new Stripe.SubscriptionService());
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
                "{{PaymentConstants.StripeMetadata.PaymentType}}": "{{PaymentConstants.PaymentTypes.Subscription}}"
              },
              "last_payment_error": {
                "message": "Card declined"
              }
            }
          }
        }
        """;
}
