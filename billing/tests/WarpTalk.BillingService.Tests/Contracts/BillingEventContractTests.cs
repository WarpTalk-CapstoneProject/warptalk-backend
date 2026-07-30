using System.Text.Json;
using FluentAssertions;
using WarpTalk.Shared.Events;

namespace WarpTalk.BillingService.Tests.Contracts;

public class BillingEventContractTests
{
    [Theory]
    [InlineData("paid", BillingEventTypes.PaymentSucceeded)]
    [InlineData("failed", BillingEventTypes.PaymentFailed)]
    [InlineData("refunded", BillingEventTypes.PaymentRefunded)]
    [InlineData("disputed", BillingEventTypes.PaymentDisputed)]
    [InlineData("cancelled", BillingEventTypes.SubscriptionCancelled)]
    public void PaymentStatusMapsToStableVersionedEventType(
        string status,
        string expectedType)
        => BillingEventTypes.ForStatus(status).Should().Be(expectedType);

    [Fact]
    public void EnvelopeRoundTripsWithCorrelationAndCausationMetadata()
    {
        var payload = new BillingPaymentEventPayload(
            "pi_123", "cs_123", "paid", 499_000m, "vnd",
            "Subscription", "user-1", "workspace-1", "pro", "monthly", null);
        var envelope = new EventEnvelope<BillingPaymentEventPayload>(
            Guid.NewGuid(),
            BillingEventTypes.PaymentSucceeded,
            1,
            DateTime.UtcNow,
            "billing-service",
            "corr-1",
            "pi_123",
            "workspace-1",
            payload);

        var json = JsonSerializer.Serialize(envelope);
        var roundTrip = JsonSerializer.Deserialize<EventEnvelope<BillingPaymentEventPayload>>(json);

        roundTrip.Should().NotBeNull();
        roundTrip!.EventType.Should().Be(BillingEventTypes.PaymentSucceeded);
        roundTrip.SchemaVersion.Should().Be(1);
        roundTrip.CorrelationId.Should().Be("corr-1");
        roundTrip.CausationId.Should().Be("pi_123");
        roundTrip.Payload.Amount.Should().Be(499_000m);
    }
}
