using System.Collections.Generic;
using System.Diagnostics.Metrics;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Helpers;

/// <summary>
/// backend#467: paid extra credits that arrived for a workspace with no live subscription.
///
/// Checkout refuses a top-up or credit pack without a live plan, so every increment here is a
/// payment that got past that gate — a checkout session opened before the plan expired, or a race
/// with the expiry worker. The money was taken; these counters are what makes a person look.
///
/// EXPORTED AS (OTel collector, namespace "warptalk"):
///   warptalk_billing_paid_credits_frozen_total{payment_type}   booked FROZEN on the latest row;
///                                                              restored when the workspace renews
///   warptalk_billing_paid_credits_unheld_total{payment_type}   no subscription row ever existed to
///                                                              hold them; the webhook fails so Stripe
///                                                              retries, and support must act
///
/// Alerted on by WarpTalkPaidCreditsFrozen / WarpTalkPaidCreditsUnheld (warptalk-infrastructure,
/// both rule files). The meter name is the service name, which is what
/// <c>AddWarpTalkObservability</c> subscribes to; an instrument on any other meter is exported
/// nowhere.
/// </summary>
public static class PaidCreditsMetrics
{
    public const string MeterName = "warptalk-billing";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Frozen = Meter.CreateCounter<long>(
        "billing.paid_credits_frozen",
        description: "Paid top-ups/credit packs booked frozen because the workspace had no live subscription.");

    private static readonly Counter<long> Unheld = Meter.CreateCounter<long>(
        "billing.paid_credits_unheld",
        description: "Paid top-ups/credit packs for a workspace that never had a subscription to hold them.");

    private static readonly string[] PaymentTypes =
        [PaymentConstants.PaymentTypes.CreditTopUp, PaymentConstants.PaymentTypes.CreditPack];

    /// <summary>
    /// Publishes every series at 0. Call once the meter provider is listening (ApplicationStarted):
    /// a series first seen at 1 has no earlier sample, so <c>increase()</c> would miss that event.
    /// </summary>
    public static void Initialize()
    {
        foreach (var paymentType in PaymentTypes)
        {
            var tag = new KeyValuePair<string, object?>("payment_type", paymentType);
            Frozen.Add(0, tag);
            Unheld.Add(0, tag);
        }
    }

    public static void RecordFrozen(string paymentType) =>
        Frozen.Add(1, new KeyValuePair<string, object?>("payment_type", paymentType));

    public static void RecordUnheld(string paymentType) =>
        Unheld.Add(1, new KeyValuePair<string, object?>("payment_type", paymentType));
}
