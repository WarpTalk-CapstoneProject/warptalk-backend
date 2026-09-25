using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// What an ended subscription's balance becomes. Before CreditFreezeService it became nothing: the
/// row went inactive with its balance on it, a renewal created a new row, and the credits — bought
/// ones included — were gone with no ledger line saying so.
/// </summary>
public class CreditFreezeServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc);

    private readonly List<Subscription> _subscriptions = new();
    private readonly List<CreditTransaction> _ledger = new();
    private readonly List<Plan> _plans = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly CreditFreezeService _service;
    private readonly Guid _workspaceId = Guid.NewGuid();

    public CreditFreezeServiceTests()
    {
        var subscriptions = new Mock<ISubscriptionRepository>();
        subscriptions
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Subscription, bool>> p, CancellationToken _) =>
                _subscriptions.Where(p.Compile()).ToList());
        subscriptions
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Subscription, bool>> p, CancellationToken _) =>
                _subscriptions.FirstOrDefault(p.Compile()));

        var ledger = new Mock<ICreditTransactionRepository>();
        ledger
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<CreditTransaction, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<CreditTransaction, bool>> p, CancellationToken _) =>
                _ledger.Where(p.Compile()).ToList());
        ledger
            .Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Callback<CreditTransaction, CancellationToken>((tx, _) => _ledger.Add(tx))
            .Returns(Task.CompletedTask);

        var plans = new Mock<IPlanRepository>();
        plans
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _plans.FirstOrDefault(p => p.Id == id));

        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptions.Object);
        _unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(ledger.Object);
        _unitOfWork.Setup(u => u.Plans).Returns(plans.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _service = new CreditFreezeService(_unitOfWork.Object, NullLogger<CreditFreezeService>.Instance);
    }

    private Plan AddPlan(int rolloverCap)
    {
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Plan", Slug = "plan", CreditsPerCycle = 1_000, RolloverCapCredits = rolloverCap };
        _plans.Add(plan);
        return plan;
    }

    private Subscription AddSubscription(Plan plan, int balance, bool active = false, string? status = null)
    {
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            UserId = Guid.NewGuid(),
            PlanId = plan.Id,
            IsActive = active,
            Status = status ?? (active ? SubscriptionConstants.SubscriptionStatuses.Active : SubscriptionConstants.SubscriptionStatuses.Expired),
            CreditsRemaining = balance,
            CurrentPeriodStart = Now.AddDays(-31),
            CurrentPeriodEnd = active ? Now.AddDays(30) : Now.AddDays(-1),
        };
        _subscriptions.Add(subscription);
        return subscription;
    }

    private void Grant(Subscription subscription, int amount, string type, string? referenceType, string? description = null) =>
        _ledger.Add(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            WorkspaceId = subscription.WorkspaceId,
            Amount = amount,
            Type = type,
            ReferenceType = referenceType,
            Description = description,
            CreatedAt = Now.AddDays(-10),
        });

    [Theory]
    [InlineData(1_000, 0, 0, 0, 0, 1_000)] // nothing purchased, no rollover: all plan credit forfeits
    [InlineData(1_000, 0, 300, 0, 300, 700)] // the rollover cap is what the plan says carries
    [InlineData(1_000, 400, 0, 400, 0, 600)] // purchased credits are spent last, so 400 of it is kept
    [InlineData(1_000, 5_000, 0, 1_000, 0, 0)] // never more than the balance is purchased
    [InlineData(1_000, 400, 10_000, 400, 600, 0)] // a generous cap keeps the whole plan remainder
    [InlineData(-10, 400, 300, 0, 0, 0)] // a debt is not frozen, forfeited or forgiven
    [InlineData(1_000, -200, 0, 0, 0, 1_000)] // a net clawback purchased nothing
    public void Split_follows_the_policy(int balance, long purchased, int cap, int purchasedKept, int planKept, int forfeited)
    {
        var split = CreditFreezeService.Split(balance, purchased, cap);

        split.PurchasedKept.Should().Be(purchasedKept);
        split.PlanKept.Should().Be(planKept);
        split.Forfeited.Should().Be(forfeited);
        if (balance > 0)
        {
            (split.Frozen + split.Forfeited).Should().Be(balance, "every credit is either kept or forfeited, never lost");
        }
    }

    [Fact]
    public async Task An_expired_subscription_freezes_purchased_credits_and_forfeits_the_plan_remainder_with_ledger_rows()
    {
        var plan = AddPlan(rolloverCap: 100);
        var expired = AddSubscription(plan, balance: 1_000);
        Grant(expired, 1_000, TransactionConstants.TransactionTypes.TopUp, TransactionConstants.ReferenceTypes.StripePayment,
            "Subscription Plan Activation: Plan");
        Grant(expired, 300, TransactionConstants.TransactionTypes.TopUp, TransactionConstants.ReferenceTypes.StripePayment,
            "Credit top-up: 300 credits purchased");
        Grant(expired, 200, TransactionConstants.TransactionTypes.TopUp, PackageCatalogConstants.ReferenceTypes.CreditPackPurchase,
            "Credit pack 'S': 200 credits");

        var split = await _service.SplitEndedSubscriptionsAsync(Now);

        split.Should().Be(1);
        expired.CreditsRemaining.Should().Be(0, "nothing on an ended row stays spendable");
        expired.FrozenCredits.Should().Be(600, "500 purchased + 100 plan credits within the rollover cap");
        expired.CreditsFrozenAt.Should().Be(Now);

        var forfeit = _ledger.Single(t => t.Type == TransactionConstants.TransactionTypes.CreditForfeit);
        forfeit.Amount.Should().Be(-400);
        forfeit.IdempotencyKey.Should().Be($"credit_forfeit:{expired.Id}");
        forfeit.ReferenceType.Should().Be(TransactionConstants.ReferenceTypes.SubscriptionExpiry);

        var freeze = _ledger.Single(t => t.Type == TransactionConstants.TransactionTypes.CreditFreeze);
        freeze.Amount.Should().Be(-600);
        freeze.BalanceAfter.Should().Be(0);
        freeze.IdempotencyKey.Should().Be($"credit_freeze:{expired.Id}");
    }

    [Fact]
    public async Task Splitting_is_idempotent()
    {
        var plan = AddPlan(rolloverCap: 0);
        AddSubscription(plan, balance: 500);

        await _service.SplitEndedSubscriptionsAsync(Now);
        var rowsAfterFirst = _ledger.Count;
        var second = await _service.SplitEndedSubscriptionsAsync(Now.AddHours(1));

        second.Should().Be(0);
        _ledger.Should().HaveCount(rowsAfterFirst);
    }

    [Fact]
    public async Task A_live_subscription_is_never_split()
    {
        var plan = AddPlan(rolloverCap: 0);
        var live = AddSubscription(plan, balance: 500, active: true);

        await _service.SplitEndedSubscriptionsAsync(Now);

        live.CreditsRemaining.Should().Be(500);
        live.CreditsFrozenAt.Should().BeNull();
        _ledger.Should().BeEmpty();
    }

    [Fact]
    public async Task An_ended_row_in_debt_is_marked_split_and_its_debt_left_alone()
    {
        var plan = AddPlan(rolloverCap: 1_000);
        var overdrawn = AddSubscription(plan, balance: -10);

        await _service.SplitEndedSubscriptionsAsync(Now);

        overdrawn.CreditsRemaining.Should().Be(-10);
        overdrawn.FrozenCredits.Should().Be(0);
        overdrawn.CreditsFrozenAt.Should().Be(Now);
        _ledger.Should().BeEmpty();
    }

    [Fact]
    public async Task Renewal_restores_frozen_credits_into_the_new_subscription()
    {
        var plan = AddPlan(rolloverCap: 0);
        var ended = AddSubscription(plan, balance: 0);
        ended.FrozenCredits = 700;
        ended.CreditsFrozenAt = Now.AddDays(-3);
        var renewed = AddSubscription(plan, balance: 1_000, active: true);

        var released = await _service.StageReleaseIntoAsync(renewed, Now);

        released.Should().Be(700);
        renewed.CreditsRemaining.Should().Be(1_700);
        ended.FrozenCredits.Should().Be(0);
        var unfreeze = _ledger.Single(t => t.Type == TransactionConstants.TransactionTypes.CreditUnfreeze);
        unfreeze.SubscriptionId.Should().Be(renewed.Id);
        unfreeze.ReferenceId.Should().Be(ended.Id);
        unfreeze.Amount.Should().Be(700);
        unfreeze.BalanceAfter.Should().Be(1_700);
        unfreeze.IdempotencyKey.Should().StartWith($"credit_unfreeze:{ended.Id}:");
    }

    [Fact]
    public async Task Restored_credits_lift_an_overage_suspension_but_not_any_other()
    {
        var plan = AddPlan(rolloverCap: 0);
        var ended = AddSubscription(plan, balance: 0);
        ended.FrozenCredits = 50;
        ended.CreditsFrozenAt = Now.AddDays(-3);
        var overdrawn = AddSubscription(plan, balance: -10, active: true);
        overdrawn.ServiceState = SubscriptionConstants.ServiceStates.Suspended;
        overdrawn.SuspendedReason = SubscriptionConstants.SuspendedReasons.OverageCap;

        await _service.StageReleaseIntoAsync(overdrawn, Now);

        overdrawn.CreditsRemaining.Should().Be(40);
        overdrawn.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        overdrawn.SuspendedReason.Should().BeNull();
    }

    [Fact]
    public async Task The_sweep_releases_only_into_a_live_subscription()
    {
        var plan = AddPlan(rolloverCap: 0);
        var ended = AddSubscription(plan, balance: 0);
        ended.FrozenCredits = 300;
        ended.CreditsFrozenAt = Now.AddDays(-3);

        (await _service.ReleaseFrozenCreditsAsync(Now)).Should().Be(0, "no live subscription: the credits stay frozen");
        ended.FrozenCredits.Should().Be(300);

        var renewed = AddSubscription(plan, balance: 1_000, active: true);
        (await _service.ReleaseFrozenCreditsAsync(Now)).Should().Be(1);
        renewed.CreditsRemaining.Should().Be(1_300);
        ended.FrozenCredits.Should().Be(0);
    }

    [Fact]
    public async Task Frozen_credits_turn_dormant_after_the_grace_window_and_are_kept()
    {
        var plan = AddPlan(rolloverCap: 0);
        var old = AddSubscription(plan, balance: 0);
        old.CurrentPeriodEnd = Now.AddDays(-31);
        old.CreditsFrozenAt = Now.AddDays(-31);
        old.FrozenCredits = 900;
        var recent = AddSubscription(plan, balance: 0);
        recent.CurrentPeriodEnd = Now.AddDays(-5);
        recent.CreditsFrozenAt = Now.AddDays(-5);
        recent.FrozenCredits = 100;

        var marked = await _service.MarkDormantAsync(Now, graceDays: 30);

        marked.Should().Be(1);
        old.FrozenCreditsDormantAt.Should().Be(Now);
        old.FrozenCredits.Should().Be(900, "dormant is a label, never a deletion");
        recent.FrozenCreditsDormantAt.Should().BeNull();
        _ledger.Should().BeEmpty();
    }
}
