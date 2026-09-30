using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.BillingService.Infrastructure.Workers;
using WarpTalk.Shared;
using WarpTalk.Shared.Coordination;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.BillingService.Tests.Application.Recurring;

/// <summary>
/// #466 — auto-renew means recurring payment. Renewal is driven by Stripe's invoice.paid, a failed
/// charge enters dunning, and the local workers never renew or expire a row Stripe still owns.
/// </summary>
public class StripeRecurringRenewalTests
{
    private static readonly DateTime PeriodEnd = DateTime.UtcNow.AddMinutes(-30);

    // ---- invoice.paid ---------------------------------------------------------------------------

    [Fact]
    public async Task Invoice_paid_renews_the_linked_row_once_even_when_Stripe_redelivers_it()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: 100_000, rolloverCap: 2_000);
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, credits: 5_000, stripeSubscriptionId: "sub_test_renew");
        var nextEnd = PeriodEnd.AddMonths(1);
        var app = world.PaymentApp();

        var paid = RecurringBillingWorld.RenewalEvent(sub, "in_test_cycle_1", paid: true, PeriodEnd, nextEnd);
        (await app.ProcessPaymentEventAsync(paid)).IsSuccess.Should().BeTrue();
        (await app.ProcessPaymentEventAsync(paid)).IsSuccess.Should().BeTrue("a redelivery is acknowledged");
        (await app.ProcessPaymentEventAsync(paid)).IsSuccess.Should().BeTrue();

        sub.CurrentPeriodEnd.Should().Be(nextEnd, "the period is the one the invoice paid for");
        sub.CurrentPeriodStart.Should().Be(PeriodEnd);
        sub.CreditsRemaining.Should().Be(2_000 + 100_000, "rollover cap carried, then exactly one cycle granted");
        world.GrantedCredits(sub).Should().Be(100_000, "one ledger row for one invoice");
        world.Ledger.Single().IdempotencyKey.Should().Be("stripe_invoice:in_test_cycle_1:cycle_grant");
        world.Payments.Should().ContainSingle(p => p.ProviderTransactionId == "in_test_cycle_1" && p.Status == PaymentConstants.PaymentStatuses.Paid);
        world.Invoices.Should().ContainSingle();
    }

    [Fact]
    public async Task A_second_cycle_is_a_second_invoice_and_a_second_grant()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: 100_000, rolloverCap: 1_000_000);
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, credits: 0, stripeSubscriptionId: "sub_test_two");
        var app = world.PaymentApp();

        await app.ProcessPaymentEventAsync(RecurringBillingWorld.RenewalEvent(sub, "in_test_a", true, PeriodEnd, PeriodEnd.AddMonths(1)));
        await app.ProcessPaymentEventAsync(RecurringBillingWorld.RenewalEvent(sub, "in_test_b", true, PeriodEnd.AddMonths(1), PeriodEnd.AddMonths(2)));

        world.GrantedCredits(sub).Should().Be(200_000);
        sub.CurrentPeriodEnd.Should().Be(PeriodEnd.AddMonths(2));
    }

    [Fact]
    public async Task Two_deliveries_racing_past_the_short_circuit_still_grant_once()
    {
        // Both deliveries read "no payment yet" before either commits. The unique ledger key (and in
        // production the unique payment key and xmin) refuse the second commit.
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: 100_000);
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, credits: 0, stripeSubscriptionId: "sub_test_race");
        var handler = world.Handler();
        var request = RecurringBillingWorld.RenewalEvent(sub, "in_test_race", true, PeriodEnd, PeriodEnd.AddMonths(1));

        PaymentEventContext Context() => new(request, sub.WorkspaceId, sub.UserId, "in_test_race", PaymentConstants.PaymentStatuses.Paid, Guid.NewGuid(), null, sub);

        (await handler.HandleAsync(Context())).IsSuccess.Should().BeTrue();
        await world.UnitOfWork.Object.SaveChangesAsync();

        // The second delivery was already past the payment check when the first committed.
        (await handler.HandleAsync(Context())).IsSuccess.Should().BeTrue();
        var secondCommit = () => world.UnitOfWork.Object.SaveChangesAsync();

        await secondCommit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*idempotency_key*");
        world.GrantedCredits(sub).Should().Be(100_000);
    }

    // ---- the #466 race: the cycle close running concurrently -----------------------------------

    [Fact]
    public async Task The_cycle_close_never_grants_a_stripe_row_so_there_is_no_double_grant()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: 100_000);
        var stripeRow = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, credits: 0, stripeSubscriptionId: "sub_test_cc");
        var invoiceRow = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Invoice, PeriodEnd, credits: 0);
        var cycleClose = CycleClose(world);

        // The worker's tick and Stripe's webhook land in the same minute, in either order.
        var closed = await cycleClose.CloseDueCyclesAsync(DateTime.UtcNow, TimeSpan.FromHours(24));
        await world.PaymentApp().ProcessPaymentEventAsync(
            RecurringBillingWorld.RenewalEvent(stripeRow, "in_test_cc", true, PeriodEnd, PeriodEnd.AddMonths(1)));
        var closedAgain = await cycleClose.CloseDueCyclesAsync(DateTime.UtcNow, TimeSpan.FromHours(24));

        closed.Value.Should().Be(1, "only the invoice row is the cycle close's");
        closedAgain.Value.Should().Be(0);
        world.GrantedCredits(stripeRow).Should().Be(100_000, "granted once, by the invoice Stripe was paid for");
        world.GrantedCredits(invoiceRow).Should().Be(100_000);
        world.Payments.Should().NotContain(p => p.SubscriptionId == stripeRow.Id && p.Provider == PaymentConstants.Providers.InternalInvoice);
    }

    [Fact]
    public async Task Closing_a_stripe_workspace_by_hand_is_refused()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan();
        var stripeRow = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, DateTime.UtcNow.AddDays(10), stripeSubscriptionId: "sub_test_manual");

        var result = await CycleClose(world).CloseWorkspaceCycleAsync(stripeRow.WorkspaceId, DateTime.UtcNow);

        result.IsSuccess.Should().BeFalse();
        world.Ledger.Should().BeEmpty();
        stripeRow.CurrentPeriodEnd.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task The_expiry_sweep_leaves_a_stripe_row_whose_renewal_is_due()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan();
        var stripeRow = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, stripeSubscriptionId: "sub_test_sweep");

        await Sweep(world);

        stripeRow.IsActive.Should().BeTrue();
        stripeRow.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
        world.Stripe.Verify(s => s.CancelNowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- invoice.payment_failed → dunning → expiry after grace ---------------------------------

    [Fact]
    public async Task A_failed_renewal_enters_dunning_keeps_the_plan_for_the_grace_and_expires_after_it()
    {
        var world = new RecurringBillingWorld { GraceDays = 3 };
        var plan = world.AddPlan();
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, credits: 700, stripeSubscriptionId: "sub_test_dunning");
        var app = world.PaymentApp();
        var failed = RecurringBillingWorld.RenewalEvent(sub, "in_test_declined", paid: false, PeriodEnd, PeriodEnd.AddMonths(1));

        (await app.ProcessPaymentEventAsync(failed)).IsSuccess.Should().BeTrue();
        var graceEnds = sub.PaymentGraceEndsAt;
        (await app.ProcessPaymentEventAsync(failed with { FailureReason = "attempt 2" })).IsSuccess.Should().BeTrue();

        sub.PaymentFailedAt.Should().NotBeNull();
        graceEnds.Should().BeCloseTo(sub.PaymentFailedAt!.Value.AddDays(3), TimeSpan.FromSeconds(1));
        sub.PaymentGraceEndsAt.Should().Be(graceEnds, "Stripe's retries do not move the grace window");
        sub.StripeSubscriptionStatus.Should().Be(SubscriptionConstants.StripeSubscriptionStatuses.PastDue);
        sub.CreditsRemaining.Should().Be(700, "no money arrived, nothing is granted");
        sub.CurrentPeriodEnd.Should().Be(PeriodEnd, "the paid-through date does not move");
        world.Payments.Should().ContainSingle(p => p.ProviderTransactionId == "in_test_declined" && p.Status == PaymentConstants.PaymentStatuses.Failed);
        world.Notifications.Verify(n => n.SendNotificationsAsync(
            It.Is<SendBillingNotificationsRequest>(r => r.UserIds.Single() == sub.UserId && r.Title == BillingMessageConstants.NotificationTitles.RenewalPaymentFailed),
            It.IsAny<CancellationToken>()), Times.Once, "the owner is told once per episode");

        // Inside the grace: the plan is in force and the sweep leaves it alone.
        sub.GrantsPlanEntitlements(DateTime.UtcNow).Should().BeTrue();
        await Sweep(world);
        sub.IsActive.Should().BeTrue();

        // After the grace: the sweep ends it and stops Stripe retrying.
        sub.PaymentGraceEndsAt = DateTime.UtcNow.AddMinutes(-1);
        await Sweep(world);
        sub.IsActive.Should().BeFalse();
        sub.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Expired);
        sub.StripeSubscriptionStatus.Should().Be(SubscriptionConstants.StripeSubscriptionStatuses.Canceled);
        world.Stripe.Verify(s => s.CancelNowAsync("sub_test_dunning", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_retry_that_succeeds_during_the_grace_renews_and_clears_dunning()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: 100_000);
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, credits: 0, stripeSubscriptionId: "sub_test_retry");
        var app = world.PaymentApp();

        await app.ProcessPaymentEventAsync(RecurringBillingWorld.RenewalEvent(sub, "in_test_retry", false, PeriodEnd, PeriodEnd.AddMonths(1)));
        await app.ProcessPaymentEventAsync(RecurringBillingWorld.RenewalEvent(sub, "in_test_retry", true, PeriodEnd, PeriodEnd.AddMonths(1)));

        sub.PaymentFailedAt.Should().BeNull();
        sub.PaymentGraceEndsAt.Should().BeNull();
        sub.StripeSubscriptionStatus.Should().Be(SubscriptionConstants.StripeSubscriptionStatuses.Active);
        sub.CurrentPeriodEnd.Should().Be(PeriodEnd.AddMonths(1));
        world.GrantedCredits(sub).Should().Be(100_000);
        world.Payments.Should().ContainSingle(p => p.ProviderTransactionId == "in_test_retry")
            .Which.Status.Should().Be(PaymentConstants.PaymentStatuses.Paid, "the failed payment row flips to paid");
    }

    // ---- toggle off / on -------------------------------------------------------------------------

    [Fact]
    public async Task Toggling_auto_renew_off_and_on_moves_Stripe_cancel_at_period_end_and_keeps_the_plan()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan();
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, DateTime.UtcNow.AddDays(12), stripeSubscriptionId: "sub_test_toggle");
        world.Stripe
            .Setup(s => s.SetCancelAtPeriodEndAsync("sub_test_toggle", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, bool cancel, CancellationToken _) => Result.Success(Snapshot(id, cancel)));
        var lifecycle = world.Lifecycle();

        var off = await lifecycle.SetAutoRenewAsync(sub.WorkspaceId, autoRenew: false);
        off.IsSuccess.Should().BeTrue();
        sub.AutoRenew.Should().BeFalse();
        sub.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active, "switching renewal off never ends a paid period");
        sub.GrantsPlanEntitlements(DateTime.UtcNow).Should().BeTrue();
        world.Stripe.Verify(s => s.SetCancelAtPeriodEndAsync("sub_test_toggle", true, It.IsAny<CancellationToken>()), Times.Once);

        var on = await lifecycle.SetAutoRenewAsync(sub.WorkspaceId, autoRenew: true);
        on.IsSuccess.Should().BeTrue();
        sub.AutoRenew.Should().BeTrue();
        world.Stripe.Verify(s => s.SetCancelAtPeriodEndAsync("sub_test_toggle", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_Stripe_refuses_the_toggle_the_row_is_left_as_it_was()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan();
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, DateTime.UtcNow.AddDays(12), stripeSubscriptionId: "sub_test_down");
        world.Stripe
            .Setup(s => s.SetCancelAtPeriodEndAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<StripeRecurringSnapshot>("Stripe is unavailable", ErrorCodes.BillingExternalServiceError));

        var result = await world.Lifecycle().SetAutoRenewAsync(sub.WorkspaceId, autoRenew: false);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingExternalServiceError);
        sub.AutoRenew.Should().BeTrue();
        world.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Stripe_ending_the_subscription_ends_the_plan_at_period_end_not_now()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan();
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, DateTime.UtcNow.AddDays(4), stripeSubscriptionId: "sub_test_deleted");

        var result = await world.Lifecycle().ApplyStripeSubscriptionChangeAsync(new StripeSubscriptionChange(
            "sub_test_deleted", "canceled", CancelAtPeriodEnd: false, Deleted: true, "cus_test_1", sub.WorkspaceId.ToString(), null));

        result.IsSuccess.Should().BeTrue();
        sub.IsActive.Should().BeTrue();
        sub.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
        sub.AutoRenew.Should().BeFalse();
        sub.GrantsPlanEntitlements(DateTime.UtcNow).Should().BeTrue("paid through its period end");
        SubscriptionOwnership.DueForExpiry(sub.CurrentPeriodEnd.AddMinutes(1), TimeSpan.FromHours(24), TimeSpan.FromDays(8))
            .Compile()(sub).Should().BeTrue("the sweep owns it once the period ends");
    }

    // ---- one-off customers are unaffected ------------------------------------------------------

    [Fact]
    public async Task A_one_off_row_cannot_be_switched_to_auto_renew_without_a_checkout()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan();
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.None, DateTime.UtcNow.AddDays(12));
        sub.AutoRenew = false;

        var result = await world.Lifecycle().SetAutoRenewAsync(sub.WorkspaceId, autoRenew: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(StripeSubscriptionLifecycleService.AutoRenewRequiresCheckoutCode);
        sub.AutoRenew.Should().BeFalse();
        world.Stripe.Verify(s => s.SetCancelAtPeriodEndAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_one_off_card_row_is_expired_at_period_end_and_never_renewed_for_free()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan();
        var legacy = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.None, PeriodEnd);

        var closed = await CycleClose(world).CloseDueCyclesAsync(DateTime.UtcNow, TimeSpan.FromHours(24));
        await Sweep(world);

        closed.Value.Should().Be(0);
        world.Ledger.Should().BeEmpty();
        legacy.IsActive.Should().BeFalse();
        legacy.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Expired);
        world.Stripe.Verify(s => s.CancelNowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_pre_466_stripe_subscription_renews_as_it_did_and_is_linked_on_the_way()
    {
        // Bought before #466: the checkout never recorded its Stripe subscription, so the renewal
        // invoice names a subscription no row knows. Today's behaviour (the activation path) is
        // kept, and the row is linked so the next cycle is Stripe-owned.
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: 100_000);
        var legacy = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.None, DateTime.UtcNow.AddMinutes(-5), credits: 10);
        var request = RecurringBillingWorld.RenewalEvent(legacy, "in_test_legacy", true, legacy.CurrentPeriodEnd, legacy.CurrentPeriodEnd.AddMonths(1))
            with { StripeSubscriptionId = "sub_test_legacy" };

        (await world.PaymentApp().ProcessPaymentEventAsync(request)).IsSuccess.Should().BeTrue();

        legacy.RenewalMode.Should().Be(SubscriptionConstants.RenewalModes.Stripe);
        legacy.StripeSubscriptionId.Should().Be("sub_test_legacy");
        legacy.CurrentPeriodEnd.Should().BeAfter(DateTime.UtcNow.AddDays(27));
        world.GrantedCredits(legacy).Should().Be(100_000);
    }

    // ---- harness -------------------------------------------------------------------------------

    private static StripeRecurringSnapshot Snapshot(string id, bool cancelAtPeriodEnd) => new(
        id, "active", cancelAtPeriodEnd, DateTime.UtcNow.AddDays(12), "cus_test_1",
        cancelAtPeriodEnd ? null : 499_000m, cancelAtPeriodEnd ? null : "vnd", DateTime.UtcNow.AddDays(12),
        "visa", "4242", 12, 2030);

    private static BillingCycleClosingService CycleClose(RecurringBillingWorld world)
    {
        var usage = new Mock<IUsageRecordRepository> { CallBase = true };
        usage.Setup(r => r.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<UsageRecord, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UsageRecord>());
        world.UnitOfWork.Setup(u => u.UsageRecordRepository).Returns(usage.Object);
        var policy = new Mock<IBillingPolicyService>();
        policy.Setup(p => p.GetPolicyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new BillingPolicyDto(0.10m));
        return new BillingCycleClosingService(world.UnitOfWork.Object, new SubscriptionDomainService(), policy.Object, NullLogger<BillingCycleClosingService>.Instance);
    }

    private static async Task Sweep(RecurringBillingWorld world)
    {
        var services = new ServiceCollection()
            .AddSingleton(world.UnitOfWork.Object)
            .AddSingleton(world.Stripe.Object)
            .AddSingleton(world.Settings.Object)
            .BuildServiceProvider();
        var worker = new SubscriptionExpirationWorker(
            services,
            NullLogger<SubscriptionExpirationWorker>.Instance,
            Options.Create(new BillingWorkerOptions { SubscriptionRenewalLookbackHours = 24, SubscriptionExpirationIntervalMinutes = 60 }),
            new DistributedLockProvider(new InProcessLeaseStore(TimeProvider.System), TimeProvider.System));
        await worker.SweepAsync(CancellationToken.None);
    }
}
