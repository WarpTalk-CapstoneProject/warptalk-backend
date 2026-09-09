using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.PaymentEventHandlers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application;

/// <summary>
/// WT-524. Changing plan destroyed time the customer had already paid for.
///
/// `CurrentPeriodEnd` was assigned "now + one cycle of the NEW plan", unconditionally. Buy a year
/// on 18 Aug 2026 — paid through 18 Aug 2027 — then upgrade to a monthly plan, and the end date
/// became 18 Sep 2026. Eleven months of collected money erased by an assignment, with the billing
/// page reporting the shorter date as though it were correct.
///
/// The rule these tests pin is only "never move the end date backwards". Crediting the unused
/// annual value against the new plan's price is proration — a real billing decision needing
/// Stripe's own support and somebody's sign-off — and is deliberately NOT what this does.
/// </summary>
public class SubscriptionPeriodEndTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<ICreditTransactionRepository> _creditTransactions = new();
    private readonly Mock<IPlanRepository> _plans = new();
    private readonly SubscriptionPaymentEventHandler _handler;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private static readonly Plan MonthlyPlan = new()
    {
        Id = Guid.NewGuid(),
        Slug = "pro",
        Name = "Pro",
        CreditsPerCycle = 10_000,
    };

    public SubscriptionPeriodEndTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_creditTransactions.Object);
        _unitOfWork.Setup(u => u.Plans).Returns(_plans.Object);

        _plans
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MonthlyPlan);

        // No OTHER active subscription rows to supersede — this workspace has exactly the one
        // being changed, which is the case the report describes.
        _subscriptions
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subscription>());

        _creditTransactions
            .Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _handler = new SubscriptionPaymentEventHandler(
            _unitOfWork.Object,
            Mock.Of<ILogger<SubscriptionPaymentEventHandler>>());
    }

    private PaymentEventContext Context(string billingCycle, Subscription subscription)
    {
        var request = new StripePaymentEventRequest(
            StripeSessionId: "cs_test_wt524",
            PaymentIntentId: "pi_test",
            Amount: 200_000m,
            Currency: PaymentConstants.Currencies.Vnd,
            UserIdStr: _userId.ToString(),
            WorkspaceIdStr: _workspaceId.ToString(),
            PaymentType: PaymentConstants.PaymentTypes.Subscription,
            Status: PaymentConstants.PaymentStatuses.Paid,
            PlanSlug: "pro",
            BillingCycle: billingCycle);

        return new PaymentEventContext(
            request,
            _workspaceId,
            _userId,
            providerTransactionId: "cs_test_wt524",
            parsedPaymentStatus: PaymentConstants.PaymentStatuses.Paid,
            paymentId: Guid.NewGuid(),
            existingPayment: null,
            subscription: subscription);
    }

    private Subscription SubscriptionPaidThrough(DateTime end) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        UserId = _userId,
        IsActive = true,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        CreditsRemaining = 5_000,
        CurrentPeriodStart = end.AddYears(-1),
        CurrentPeriodEnd = end,
    };

    [Fact]
    public async Task UpgradingAnAnnualPlanToAMonthlyOneKeepsTheYearAlreadyPaidFor()
    {
        // The exact shape of the report: an annual subscription with eleven months left, switched
        // to a monthly plan.
        var paidThrough = DateTime.UtcNow.AddMonths(11);
        var subscription = SubscriptionPaidThrough(paidThrough);

        var result = await _handler.HandleAsync(Context(PaymentConstants.PriceIntervals.Month, subscription));

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.CurrentPeriodEnd.Should().Be(
            paidThrough,
            "a plan change must not take back time the customer has already been charged for");
    }

    [Fact]
    public async Task MovingToAYearlyPlanStillExtendsTheEndDate()
    {
        // The mirror case. The guard must not freeze the date — a longer cycle still wins.
        var paidThrough = DateTime.UtcNow.AddDays(20);
        var subscription = SubscriptionPaidThrough(paidThrough);

        var result = await _handler.HandleAsync(Context(PaymentConstants.PriceIntervals.Year, subscription));

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.CurrentPeriodEnd.Should().BeAfter(
            DateTime.UtcNow.AddMonths(11),
            "buying a year from twenty days out must still buy a year");
    }

    [Fact]
    public async Task AnOrdinaryMonthlyRenewalStillRollsForwardAMonth()
    {
        // The common path, and the one a careless guard would break by pinning the old date.
        var paidThrough = DateTime.UtcNow.AddDays(3);
        var subscription = SubscriptionPaidThrough(paidThrough);

        var result = await _handler.HandleAsync(Context(PaymentConstants.PriceIntervals.Month, subscription));

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.CurrentPeriodEnd.Should().BeAfter(
            DateTime.UtcNow.AddDays(25),
            "renewing a month before the old period ends must extend, not stand still");
    }

    [Fact]
    public async Task ALapsedSubscriptionTakesTheNewPeriodRatherThanItsExpiredOne()
    {
        // Expired time is not time the customer still holds, so the old date must not win here.
        var subscription = SubscriptionPaidThrough(DateTime.UtcNow.AddMonths(-2));

        var result = await _handler.HandleAsync(Context(PaymentConstants.PriceIntervals.Month, subscription));

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.CurrentPeriodEnd.Should().BeAfter(
            DateTime.UtcNow,
            "paying again after lapsing must start a period that has not already ended");
    }
}
