using System.Collections.Generic;
using System.Diagnostics.Metrics;
using FluentAssertions;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Domain.Constants;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Helpers;

/// <summary>
/// backend#467: the alert reads warptalk_billing_paid_credits_{frozen,unheld}_total. An instrument
/// on any meter but the service's own is created, incremented and exported nowhere.
/// </summary>
public class PaidCreditsMetricsTests
{
    [Fact]
    public void Both_counters_are_on_the_service_meter_and_start_at_zero()
    {
        var seen = new List<(string Instrument, long Value, object? PaymentType)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "warptalk-billing" && instrument.Name.StartsWith("billing.paid_credits_"))
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            object? type = null;
            foreach (var tag in tags)
                if (tag.Key == "payment_type") type = tag.Value;
            seen.Add((instrument.Name, value, type));
        });
        listener.Start();

        PaidCreditsMetrics.Initialize();
        PaidCreditsMetrics.RecordFrozen(PaymentConstants.PaymentTypes.CreditPack);

        seen.Should().Contain(("billing.paid_credits_frozen", 0L, PaymentConstants.PaymentTypes.CreditTopUp));
        seen.Should().Contain(("billing.paid_credits_unheld", 0L, PaymentConstants.PaymentTypes.CreditPack));
        seen.Should().Contain(("billing.paid_credits_frozen", 1L, PaymentConstants.PaymentTypes.CreditPack));
    }
}
