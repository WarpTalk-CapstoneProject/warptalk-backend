using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

/// <summary>
/// #466 — how the plan's subscription webhooks are routed. No test here talks to Stripe: the
/// invoice carries the subscription's metadata (parent.subscription_details), so the SDK is never
/// asked, and the events are parsed unsigned as in Development.
/// </summary>
public class StripeRecurringWebhookTests
{
    private static readonly string WorkspaceId = Guid.NewGuid().ToString();
    private static readonly string UserId = Guid.NewGuid().ToString();

    [Fact]
    public async Task The_first_invoice_of_a_plan_is_not_a_second_activation()
    {
        var (service, payments, _) = Build();

        var result = await service.HandleWebhookAsync(InvoiceEvent("invoice.paid", "subscription_create", "in_test_first"), string.Empty);

        result.IsSuccess.Should().BeTrue();
        payments.Verify(p => p.ProcessPaymentEventAsync(It.IsAny<StripePaymentEventRequest>()), Times.Never,
            "checkout.session.completed activates the plan; processing this too granted the plan's credits twice");
    }

    [Fact]
    public async Task A_paid_cycle_invoice_is_a_renewal_keyed_by_the_invoice()
    {
        var (service, payments, _) = Build();
        StripePaymentEventRequest? captured = null;
        payments.Setup(p => p.ProcessPaymentEventAsync(It.IsAny<StripePaymentEventRequest>()))
            .Callback<StripePaymentEventRequest>(r => captured = r)
            .ReturnsAsync(Result.Success());

        (await service.HandleWebhookAsync(InvoiceEvent("invoice.paid", "subscription_cycle", "in_test_cycle"), string.Empty)).IsSuccess.Should().BeTrue();

        captured.Should().NotBeNull();
        captured!.PaymentType.Should().Be(PaymentConstants.PaymentTypes.SubscriptionRenewal);
        captured.Status.Should().Be(PaymentConstants.PaymentStatuses.Paid);
        captured.PaymentIntentId.Should().Be("in_test_cycle");
        captured.StripeSubscriptionId.Should().Be("sub_test_plan");
        captured.StripeCustomerId.Should().Be("cus_test_plan");
        captured.WorkspaceIdStr.Should().Be(WorkspaceId);
        captured.Amount.Should().Be(499_000m);
        captured.PeriodStart.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790000000).UtcDateTime);
        captured.PeriodEnd.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792592000).UtcDateTime);
    }

    [Fact]
    public async Task A_failed_cycle_invoice_starts_dunning()
    {
        var (service, payments, _) = Build();
        StripePaymentEventRequest? captured = null;
        payments.Setup(p => p.ProcessPaymentEventAsync(It.IsAny<StripePaymentEventRequest>()))
            .Callback<StripePaymentEventRequest>(r => captured = r)
            .ReturnsAsync(Result.Success());

        (await service.HandleWebhookAsync(InvoiceEvent("invoice.payment_failed", "subscription_cycle", "in_test_declined"), string.Empty)).IsSuccess.Should().BeTrue();

        captured!.PaymentType.Should().Be(PaymentConstants.PaymentTypes.SubscriptionRenewal);
        captured.Status.Should().Be(PaymentConstants.PaymentStatuses.Failed);
        captured.FailureReason.Should().Contain("attempt 2");
        captured.Amount.Should().Be(499_000m, "the amount due, since nothing was paid");
    }

    [Theory]
    [InlineData("customer.subscription.deleted", true)]
    [InlineData("customer.subscription.updated", false)]
    public async Task A_plan_subscription_change_goes_to_the_lifecycle_and_never_becomes_a_payment(string type, bool deleted)
    {
        var (service, payments, lifecycle) = Build();
        StripeSubscriptionChange? change = null;
        lifecycle.Setup(l => l.ApplyStripeSubscriptionChangeAsync(It.IsAny<StripeSubscriptionChange>(), It.IsAny<CancellationToken>()))
            .Callback<StripeSubscriptionChange, CancellationToken>((c, _) => change = c)
            .ReturnsAsync(Result.Success());

        (await service.HandleWebhookAsync(SubscriptionEvent(type), string.Empty)).IsSuccess.Should().BeTrue();

        change.Should().NotBeNull();
        change!.StripeSubscriptionId.Should().Be("sub_test_plan");
        change.Deleted.Should().Be(deleted);
        change.CancelAtPeriodEnd.Should().BeTrue();
        change.WorkspaceIdStr.Should().Be(WorkspaceId);
        payments.Verify(p => p.ProcessPaymentEventAsync(It.IsAny<StripePaymentEventRequest>()), Times.Never);
    }

    private static (StripeWebhookService Service, Mock<IPaymentAppService> Payments, Mock<IStripeSubscriptionLifecycleService> Lifecycle) Build()
    {
        var payments = new Mock<IPaymentAppService>();
        payments.Setup(p => p.ProcessPaymentEventAsync(It.IsAny<StripePaymentEventRequest>())).ReturnsAsync(Result.Success());
        var lifecycle = new Mock<IStripeSubscriptionLifecycleService>();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PaymentConstants.StripeConfigKeys.WebhookSecret] = string.Empty })
            .Build();

        var service = new StripeWebhookService(
            payments.Object,
            configuration,
            environment.Object,
            Mock.Of<ILogger<StripeWebhookService>>(),
            new Stripe.SubscriptionService(),
            lifecycle.Object);
        return (service, payments, lifecycle);
    }

    private static string Metadata() =>
        $$"""
        {
          "{{PaymentConstants.StripeMetadata.UserId}}": "{{UserId}}",
          "{{PaymentConstants.StripeMetadata.WorkspaceId}}": "{{WorkspaceId}}",
          "{{PaymentConstants.StripeMetadata.PaymentType}}": "{{PaymentConstants.PaymentTypes.Subscription}}",
          "{{PaymentConstants.StripeMetadata.PlanSlug}}": "startup",
          "{{PaymentConstants.StripeMetadata.BillingCycle}}": "monthly",
          "{{PaymentConstants.StripeMetadata.AutoRenew}}": "true"
        }
        """;

    private static string InvoiceEvent(string type, string billingReason, string invoiceId) =>
        $$"""
        {
          "id": "evt_{{invoiceId}}",
          "object": "event",
          "type": "{{type}}",
          "data": {
            "object": {
              "id": "{{invoiceId}}",
              "object": "invoice",
              "billing_reason": "{{billingReason}}",
              "currency": "vnd",
              "amount_paid": {{(type == "invoice.paid" ? 499000 : 0)}},
              "amount_due": 499000,
              "attempt_count": 2,
              "customer": "cus_test_plan",
              "parent": {
                "type": "subscription_details",
                "subscription_details": {
                  "subscription": "sub_test_plan",
                  "metadata": {{Metadata()}}
                }
              },
              "lines": {
                "object": "list",
                "data": [
                  {
                    "id": "il_test_1",
                    "object": "line_item",
                    "period": { "start": 1790000000, "end": 1792592000 }
                  }
                ]
              }
            }
          }
        }
        """;

    private static string SubscriptionEvent(string type) =>
        $$"""
        {
          "id": "evt_sub_change",
          "object": "event",
          "type": "{{type}}",
          "data": {
            "object": {
              "id": "sub_test_plan",
              "object": "subscription",
              "status": "active",
              "cancel_at_period_end": true,
              "customer": "cus_test_plan",
              "metadata": {{Metadata()}},
              "items": { "object": "list", "data": [] }
            }
          }
        }
        """;
}
