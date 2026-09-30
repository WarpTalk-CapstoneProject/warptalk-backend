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
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application;

/// <summary>
/// WT-878 — defence in depth behind the checkout allowlist. A paid checkout event names a plan by
/// slug; the handler used to grant that plan, its period and its CreditsPerCycle on the slug alone,
/// whatever was paid and whether or not the plan was still on sale. It now refuses both.
/// </summary>
public class SubscriptionPaymentEventHandlerActivationGuardTests
{
    private const decimal MonthlyPrice = 5_000_000m;
    private const decimal YearlyPrice = MonthlyPrice * 12m * 0.79m;

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<ICreditTransactionRepository> _creditTransactions = new();
    private readonly Mock<IPlanRepository> _plans = new();
    private readonly Mock<ICouponRepository> _coupons = new();
    private readonly SubscriptionPaymentEventHandler _handler;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private readonly Plan _plan = new()
    {
        Id = Guid.NewGuid(),
        Slug = "enterprise",
        Name = "Enterprise",
        Price = MonthlyPrice,
        Currency = PaymentConstants.Currencies.VndAccounting,
        BillingCycle = SubscriptionConstants.BillingCycles.Monthly,
        CreditsPerCycle = 700_000,
        IsActive = true,
    };

    public SubscriptionPaymentEventHandlerActivationGuardTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_creditTransactions.Object);
        _unitOfWork.Setup(u => u.Plans).Returns(_plans.Object);
        _unitOfWork.Setup(u => u.Coupons).Returns(_coupons.Object);

        _plans
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _plan);
        _subscriptions
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subscription>());
        _subscriptions
            .Setup(r => r.AddAsync(It.IsAny<Subscription>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _creditTransactions
            .Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _handler = new SubscriptionPaymentEventHandler(
            _unitOfWork.Object,
            Mock.Of<ILogger<SubscriptionPaymentEventHandler>>());
    }

    private PaymentEventContext Context(
        decimal amount,
        string paymentType = PaymentConstants.PaymentTypes.Subscription,
        string billingCycle = PaymentConstants.BillingCycles.Yearly,
        string currency = PaymentConstants.Currencies.Vnd,
        string couponId = "",
        Subscription? subscription = null)
    {
        var request = new StripePaymentEventRequest(
            StripeSessionId: "cs_test_wt878",
            PaymentIntentId: "pi_test",
            Amount: amount,
            Currency: currency,
            UserIdStr: _userId.ToString(),
            WorkspaceIdStr: _workspaceId.ToString(),
            PaymentType: paymentType,
            Status: PaymentConstants.PaymentStatuses.Paid,
            PlanSlug: "enterprise",
            BillingCycle: billingCycle,
            CouponId: couponId);

        return new PaymentEventContext(
            request,
            _workspaceId,
            _userId,
            providerTransactionId: "cs_test_wt878",
            parsedPaymentStatus: PaymentConstants.PaymentStatuses.Paid,
            paymentId: Guid.NewGuid(),
            existingPayment: null,
            subscription: subscription);
    }

    private Subscription ExistingSubscription() => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        UserId = _userId,
        IsActive = true,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        CreditsRemaining = 5_000,
        CurrentPeriodStart = DateTime.UtcNow.AddDays(-10),
        CurrentPeriodEnd = DateTime.UtcNow.AddDays(20),
    };

    private void VerifyNothingGranted()
    {
        _creditTransactions.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Never);
        _subscriptions.Verify(r => r.AddAsync(It.IsAny<Subscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_reported_exploit_is_refused_and_grants_nothing()
    {
        // SubscriptionUpdate, Enterprise, yearly, paid 15,000 VND — onto an existing plan.
        var subscription = ExistingSubscription();
        var endBefore = subscription.CurrentPeriodEnd;

        var context = Context(15_000m, PaymentConstants.PaymentTypes.SubscriptionUpdate, subscription: subscription);
        var result = await _handler.HandleAsync(context);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInvalidAmount);
        subscription.CreditsRemaining.Should().Be(5_000);
        subscription.CurrentPeriodEnd.Should().Be(endBefore);
        subscription.PlanId.Should().NotBe(_plan.Id);
        context.SubscriptionChanged.Should().BeFalse();
        VerifyNothingGranted();
    }

    [Fact]
    public async Task An_underpaid_new_subscription_is_refused()
    {
        var result = await _handler.HandleAsync(Context(MonthlyPrice, billingCycle: PaymentConstants.BillingCycles.Yearly));

        result.IsSuccess.Should().BeFalse("a month's money does not buy the yearly period");
        result.ErrorCode.Should().Be(ErrorCodes.BillingInvalidAmount);
        VerifyNothingGranted();
    }

    [Fact]
    public async Task A_payment_in_another_currency_is_refused()
    {
        var result = await _handler.HandleAsync(Context(YearlyPrice, currency: PaymentConstants.Currencies.Usd));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInvalidAmount);
        VerifyNothingGranted();
    }

    [Fact]
    public async Task An_inactive_plan_is_never_activated_whatever_was_paid()
    {
        _plan.IsActive = false;
        var subscription = ExistingSubscription();

        var result = await _handler.HandleAsync(Context(YearlyPrice, subscription: subscription));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingPlanInactive);
        subscription.CreditsRemaining.Should().Be(5_000);
        VerifyNothingGranted();
    }

    [Theory]
    [InlineData(PaymentConstants.BillingCycles.Yearly)]
    [InlineData(PaymentConstants.BillingCycles.Monthly)]
    public async Task The_full_price_still_activates(string cycle)
    {
        var paid = cycle == PaymentConstants.BillingCycles.Yearly ? YearlyPrice : MonthlyPrice;
        var context = Context(paid, billingCycle: cycle);

        var result = await _handler.HandleAsync(context);

        result.IsSuccess.Should().BeTrue(result.Error);
        context.SubscriptionChanged.Should().BeTrue();
        _creditTransactions.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_price_rounded_by_Stripe_to_whole_dong_still_activates()
    {
        _plan.Price = 499_999m; // × 12 × 0.79 = 4,739,990.52 — Stripe charges whole VND.

        var result = await _handler.HandleAsync(Context(4_739_990m));

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task A_coupon_the_checkout_applied_lowers_the_expected_amount()
    {
        var coupon = new Coupon
        {
            Id = Guid.NewGuid(),
            DiscountType = PackageCatalogConstants.DiscountTypes.Percent,
            PercentOff = 50m,
        };
        _coupons.Setup(r => r.GetByIdAsync(coupon.Id, It.IsAny<CancellationToken>())).ReturnsAsync(coupon);

        var result = await _handler.HandleAsync(Context(YearlyPrice / 2m, couponId: coupon.Id.ToString()));

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task A_coupon_does_not_excuse_paying_less_than_its_discount()
    {
        var coupon = new Coupon
        {
            Id = Guid.NewGuid(),
            DiscountType = PackageCatalogConstants.DiscountTypes.Percent,
            PercentOff = 10m,
        };
        _coupons.Setup(r => r.GetByIdAsync(coupon.Id, It.IsAny<CancellationToken>())).ReturnsAsync(coupon);

        var result = await _handler.HandleAsync(Context(15_000m, couponId: coupon.Id.ToString()));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInvalidAmount);
        VerifyNothingGranted();
    }

    [Fact]
    public async Task An_unknown_coupon_is_refused()
    {
        var result = await _handler.HandleAsync(Context(15_000m, couponId: Guid.NewGuid().ToString()));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInvalidAmount);
        VerifyNothingGranted();
    }
}
