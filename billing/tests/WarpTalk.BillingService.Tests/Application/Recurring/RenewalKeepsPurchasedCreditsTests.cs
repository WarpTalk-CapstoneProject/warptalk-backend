using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.BillingService.Infrastructure.Services;

namespace WarpTalk.BillingService.Tests.Application.Recurring;

/// <summary>
/// WT-878 (bug B), through both renewal paths: the Stripe renewal (invoice.paid) and the invoice
/// cycle close. Purchased credits carry over whole; only plan leftover above the rollover cap is
/// removed, and what is removed is a credit_forfeit ledger row rather than a silent jump in
/// balance_after.
/// </summary>
public class RenewalKeepsPurchasedCreditsTests
{
    private const int Cap = 700_000;
    private const int PerCycle = 700_000;
    private static readonly DateTime PeriodEnd = DateTime.UtcNow.AddMinutes(-30);

    private static void BookTopUp(RecurringBillingWorld world, Subscription sub, int credits) =>
        world.Ledger.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            SubscriptionId = sub.Id,
            WorkspaceId = sub.WorkspaceId,
            UserId = sub.UserId,
            Amount = credits,
            Type = TransactionConstants.TransactionTypes.TopUp,
            Description = string.Format(
                CultureInfo.InvariantCulture,
                BillingMessageConstants.SuccessMessages.CreditTopUpGrantedTemplate,
                credits.ToString("N0", CultureInfo.InvariantCulture)),
            ReferenceType = TransactionConstants.ReferenceTypes.Payment,
            CreatedAt = PeriodEnd.AddDays(-10),
        });

    private static void BookCreditPack(RecurringBillingWorld world, Subscription sub, int credits) =>
        world.Ledger.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            SubscriptionId = sub.Id,
            WorkspaceId = sub.WorkspaceId,
            UserId = sub.UserId,
            Amount = credits,
            Type = TransactionConstants.TransactionTypes.TopUp,
            Description = "Credit pack",
            ReferenceType = PackageCatalogConstants.ReferenceTypes.CreditPackPurchase,
            CreatedAt = PeriodEnd.AddDays(-10),
        });

    private static List<CreditTransaction> Forfeits(RecurringBillingWorld world, Subscription sub) =>
        world.Ledger
            .Where(t => t.SubscriptionId == sub.Id && t.Type == TransactionConstants.TransactionTypes.CreditForfeit)
            .ToList();

    // ---- Stripe renewal (invoice.paid) ----------------------------------------------------------

    [Fact]
    public async Task Stripe_renewal_keeps_purchased_credits_above_the_cap()
    {
        // 900k left, 300k of it a top-up. Plan leftover 600k is under the 700k cap: nothing is lost.
        // Before WT-878 the balance was capped to 700k and 200k paid credits disappeared.
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: PerCycle, rolloverCap: Cap);
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, credits: 900_000, stripeSubscriptionId: "sub_test_wt878_keep");
        BookTopUp(world, sub, 300_000);

        var result = await world.PaymentApp().ProcessPaymentEventAsync(
            RecurringBillingWorld.RenewalEvent(sub, "in_test_wt878_keep", paid: true, PeriodEnd, PeriodEnd.AddMonths(1)));

        result.IsSuccess.Should().BeTrue();
        sub.CreditsRemaining.Should().Be(900_000 + PerCycle);
        Forfeits(world, sub).Should().BeEmpty("nothing was removed, so nothing is forfeited");
        var grant = world.Ledger.Single(t => t.IdempotencyKey == "stripe_invoice:in_test_wt878_keep:cycle_grant");
        grant.Amount.Should().Be(PerCycle);
        grant.BalanceAfter.Should().Be(sub.CreditsRemaining);
    }

    [Fact]
    public async Task Stripe_renewal_forfeits_only_plan_leftover_above_the_cap_with_a_ledger_row()
    {
        // 1.2M left, 300k a top-up: plan leftover 900k → 700k carried, 200k forfeited, 300k kept whole.
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: PerCycle, rolloverCap: Cap);
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, credits: 1_200_000, stripeSubscriptionId: "sub_test_wt878_cap");
        BookTopUp(world, sub, 300_000);

        await world.PaymentApp().ProcessPaymentEventAsync(
            RecurringBillingWorld.RenewalEvent(sub, "in_test_wt878_cap", paid: true, PeriodEnd, PeriodEnd.AddMonths(1)));

        sub.CreditsRemaining.Should().Be(300_000 + Cap + PerCycle);
        var forfeit = Forfeits(world, sub).Should().ContainSingle().Subject;
        forfeit.Amount.Should().Be(-200_000, "the forfeit row is exactly what the cap removed");
        forfeit.ReferenceType.Should().Be(TransactionConstants.ReferenceTypes.CycleRollover);
        forfeit.BalanceAfter.Should().Be(1_000_000);
        forfeit.IdempotencyKey.Should().Be("stripe_invoice:in_test_wt878_cap:rollover_forfeit");

        // The ledger adds up: balance before, minus the forfeit, plus the grant = balance after.
        var grant = world.Ledger.Single(t => t.IdempotencyKey == "stripe_invoice:in_test_wt878_cap:cycle_grant");
        (1_200_000 + forfeit.Amount + grant.Amount).Should().Be(grant.BalanceAfter);
    }

    [Fact]
    public async Task Stripe_renewal_of_a_negative_balance_is_unchanged_and_forfeits_nothing()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: PerCycle, rolloverCap: Cap);
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, PeriodEnd, credits: -40_000, stripeSubscriptionId: "sub_test_wt878_debt");
        BookTopUp(world, sub, 300_000);

        await world.PaymentApp().ProcessPaymentEventAsync(
            RecurringBillingWorld.RenewalEvent(sub, "in_test_wt878_debt", paid: true, PeriodEnd, PeriodEnd.AddMonths(1)));

        sub.CreditsRemaining.Should().Be(PerCycle);
        Forfeits(world, sub).Should().BeEmpty();
    }

    // ---- invoice cycle close --------------------------------------------------------------------

    [Fact]
    public async Task Cycle_close_keeps_credit_pack_credits_and_writes_the_forfeit_before_the_grant()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: PerCycle, rolloverCap: Cap);
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Invoice, PeriodEnd, credits: 1_200_000);
        BookCreditPack(world, sub, 300_000);

        var closed = await CycleClose(world).CloseDueCyclesAsync(DateTime.UtcNow, TimeSpan.FromHours(24));

        closed.Value.Should().Be(1);
        sub.CreditsRemaining.Should().Be(300_000 + Cap + PerCycle);
        var forfeit = Forfeits(world, sub).Should().ContainSingle().Subject;
        forfeit.Amount.Should().Be(-200_000);
        forfeit.BalanceAfter.Should().Be(1_000_000);
        forfeit.IdempotencyKey.Should().Be(CycleRenewalCredits.CycleCloseForfeitKey(sub.Id, PeriodEnd));
        world.Ledger.IndexOf(forfeit).Should().BeLessThan(
            world.Ledger.FindIndex(t => t.SubscriptionId == sub.Id && t.Type == TransactionConstants.TransactionTypes.TopUp && t.ReferenceType != PackageCatalogConstants.ReferenceTypes.CreditPackPurchase),
            "the forfeit is staged before the cycle's grant");
    }

    [Fact]
    public async Task Cycle_close_under_the_cap_writes_no_forfeit_row()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: PerCycle, rolloverCap: Cap);
        var sub = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Invoice, PeriodEnd, credits: 500_000);

        await CycleClose(world).CloseDueCyclesAsync(DateTime.UtcNow, TimeSpan.FromHours(24));

        sub.CreditsRemaining.Should().Be(500_000 + PerCycle);
        Forfeits(world, sub).Should().BeEmpty();
    }

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
}
