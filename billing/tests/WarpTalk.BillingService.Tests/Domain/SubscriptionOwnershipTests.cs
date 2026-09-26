using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Tests.Domain;

/// <summary>
/// #466 — the expiry sweep and the cycle close must never both own a row. Prod row fac2906b… was
/// auto_renew = t and was EXPIRED at 10:55, seven minutes after its period ended at 10:48, because
/// the expiry sweep happened to run before the cycle close. These pin who owns each transition.
/// </summary>
public class SubscriptionOwnershipTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 10, 55, 22, DateTimeKind.Utc);
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);
    private static readonly TimeSpan StripeMargin = TimeSpan.FromDays(8);

    private static bool DueForExpiry(Subscription s, DateTime? now = null) =>
        SubscriptionOwnership.DueForExpiry(now ?? Now, Lookback, StripeMargin).Compile()(s);

    private static bool DueForCycleClose(Subscription s, DateTime? now = null) =>
        SubscriptionOwnership.DueForCycleClose(now ?? Now, (now ?? Now) - Lookback).Compile()(s);

    private static Subscription Row(string renewalMode, bool autoRenew = true, DateTime? periodEnd = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        IsActive = true,
        AutoRenew = autoRenew,
        RenewalMode = renewalMode,
        CurrentPeriodStart = (periodEnd ?? Now.AddMinutes(-7)).AddMonths(-1),
        CurrentPeriodEnd = periodEnd ?? Now.AddMinutes(-7),
        StripeSubscriptionId = renewalMode == SubscriptionConstants.RenewalModes.Stripe ? "sub_test_" + Guid.NewGuid().ToString("N") : null,
    };

    [Fact]
    public void The_prod_row_an_auto_renew_invoice_subscription_seven_minutes_past_its_period_is_renewed_not_expired()
    {
        var row = Row(SubscriptionConstants.RenewalModes.Invoice);

        DueForCycleClose(row).Should().BeTrue();
        DueForExpiry(row).Should().BeFalse("the cycle close owns it until its lookback runs out");
    }

    [Fact]
    public void An_invoice_row_the_cycle_close_gave_up_on_is_then_the_sweeps()
    {
        var row = Row(SubscriptionConstants.RenewalModes.Invoice, periodEnd: Now - Lookback - TimeSpan.FromMinutes(1));

        DueForCycleClose(row).Should().BeFalse();
        DueForExpiry(row).Should().BeTrue();
    }

    [Fact]
    public void A_stripe_row_is_never_the_cycle_closes_and_waits_for_its_webhook()
    {
        var row = Row(SubscriptionConstants.RenewalModes.Stripe);

        DueForCycleClose(row).Should().BeFalse("Stripe charges a card customer; the cycle close would grant a cycle nobody paid for");
        DueForExpiry(row).Should().BeFalse("invoice.paid or invoice.payment_failed is still due");
    }

    [Fact]
    public void A_stripe_row_whose_webhook_never_came_is_expired_after_the_safety_margin()
    {
        var row = Row(SubscriptionConstants.RenewalModes.Stripe, periodEnd: Now - StripeMargin - TimeSpan.FromMinutes(1));

        DueForExpiry(row).Should().BeTrue();
    }

    [Fact]
    public void A_stripe_row_in_dunning_keeps_its_plan_until_the_grace_ends_and_is_expired_after()
    {
        var row = Row(SubscriptionConstants.RenewalModes.Stripe, periodEnd: Now.AddDays(-2));
        row.PaymentFailedAt = Now.AddDays(-2);
        row.PaymentGraceEndsAt = Now.AddDays(5);

        DueForExpiry(row).Should().BeFalse();
        row.GrantsPlanEntitlements(Now).Should().BeTrue("the grace window keeps the plan in force while Stripe retries");

        var afterGrace = Now.AddDays(5).AddMinutes(1);
        DueForExpiry(row, afterGrace).Should().BeTrue();
        row.GrantsPlanEntitlements(afterGrace).Should().BeFalse();
    }

    [Fact]
    public void A_stripe_row_with_auto_renew_off_ends_at_period_end()
    {
        var row = Row(SubscriptionConstants.RenewalModes.Stripe, autoRenew: false);

        DueForExpiry(row).Should().BeTrue();
        DueForCycleClose(row).Should().BeFalse();
    }

    [Fact]
    public void A_stripe_row_whose_subscription_stripe_ended_is_the_sweeps_at_period_end()
    {
        var row = Row(SubscriptionConstants.RenewalModes.Stripe);
        row.StripeSubscriptionStatus = SubscriptionConstants.StripeSubscriptionStatuses.Canceled;

        DueForExpiry(row).Should().BeTrue();
    }

    [Fact]
    public void A_one_off_card_row_keeps_todays_end_of_period_behaviour()
    {
        // Backfilled 'none' (first payment was Stripe). auto_renew may still read true on these
        // pre-#466 rows; nothing renews them without a charge any more.
        var row = Row(SubscriptionConstants.RenewalModes.None, autoRenew: true);

        DueForCycleClose(row).Should().BeFalse("a card customer is never granted a cycle without a payment");
        DueForExpiry(row).Should().BeTrue();

        var inPeriod = Row(SubscriptionConstants.RenewalModes.None, periodEnd: Now.AddDays(3));
        DueForExpiry(inPeriod).Should().BeFalse();
    }

    [Fact]
    public void No_row_is_ever_owned_by_both_workers()
    {
        var modes = new[] { SubscriptionConstants.RenewalModes.Invoice, SubscriptionConstants.RenewalModes.Stripe, SubscriptionConstants.RenewalModes.None };
        var offsets = new[] { -60d * 24, -25, -24, -23, -1, -0.1, 0.1, 5 };
        var statuses = new[] { SubscriptionConstants.SubscriptionStatuses.Active, SubscriptionConstants.SubscriptionStatuses.Cancelled };

        foreach (var mode in modes)
        foreach (var autoRenew in new[] { true, false })
        foreach (var hours in offsets)
        foreach (var status in statuses)
        foreach (var dunning in new[] { false, true })
        {
            var row = Row(mode, autoRenew, Now.AddHours(hours));
            row.Status = status;
            if (dunning)
            {
                row.PaymentFailedAt = row.CurrentPeriodEnd;
                row.PaymentGraceEndsAt = row.CurrentPeriodEnd.AddDays(7);
            }

            (DueForCycleClose(row) && DueForExpiry(row)).Should().BeFalse(
                $"mode={mode} autoRenew={autoRenew} offsetHours={hours} status={status} dunning={dunning}");
        }
    }

    [Fact]
    public void Both_predicates_translate_to_sql()
    {
        using var context = new BillingDbContext(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);

        var expiry = context.Subscriptions.Where(SubscriptionOwnership.DueForExpiry(Now, Lookback, StripeMargin)).ToQueryString();
        var cycle = context.Subscriptions.Where(SubscriptionOwnership.DueForCycleClose(Now, Now - Lookback)).ToQueryString();

        expiry.Should().Contain("renewal_mode").And.Contain("payment_grace_ends_at").And.Contain("stripe_subscription_status");
        cycle.Should().Contain("renewal_mode");
    }
}
