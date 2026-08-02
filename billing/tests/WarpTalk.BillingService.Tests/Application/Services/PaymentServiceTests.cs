using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class PaymentServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IPaymentRepository> _mockPaymentRepo;
    private readonly Mock<ISubscriptionRepository> _mockSubRepo;
    private readonly Mock<IPlanRepository> _mockPlanRepo;
    private readonly Mock<ICreditTransactionRepository> _mockCreditTxRepo;
    private readonly PaymentService _paymentService;

    public PaymentServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockPaymentRepo = new Mock<IPaymentRepository>();
        _mockSubRepo = new Mock<ISubscriptionRepository>();
        _mockPlanRepo = new Mock<IPlanRepository>();
        _mockCreditTxRepo = new Mock<ICreditTransactionRepository>();

        _mockUnitOfWork.Setup(u => u.PaymentRepository).Returns(_mockPaymentRepo.Object);
        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockCreditTxRepo.Object);

        _paymentService = new PaymentService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<PaymentService>>().Object,
            CreateBillingPolicyService());
    }

    private static IBillingPolicyService CreateBillingPolicyService()
    {
        var billingPolicyService = new Mock<IBillingPolicyService>();
        billingPolicyService
            .Setup(s => s.GetPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPolicyDto(0.10m));
        return billingPolicyService.Object;
    }

    // ─────────────────────────────────────────────
    //  HandleWebhookAsync Tests
    //  NOTE: HandleWebhookAsync uses GetWithSubscriptionAsync (eager-load nav)
    //  and GetWithSubscriptionAndPlanAsync inside the concurrency retry block.
    //  We mock those specific repo methods here.
    // ─────────────────────────────────────────────

    [Fact]
    public async Task HandleWebhookAsync_InvalidOrderCode_ShouldReturnFailure()
    {
        // Service returns ErrorCodes.ValidationError = "VALIDATION_ERROR" for invalid GUID
        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest("not-a-guid", "PAID", "tx123"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _mockPaymentRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleWebhookAsync_PaymentNotFound_ShouldReturnFailure()
    {
        var paymentId = Guid.NewGuid();
        // HandleWebhookAsync uses GetWithSubscriptionAsync, not GetByIdAsync
        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task HandleWebhookAsync_Idempotent_Should_Ignore_If_AlreadyPaid()
    {
        var paymentId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();

        // First call: GetWithSubscriptionAsync to verify subscription nav is loaded
        var sub = new Subscription { Id = subscriptionId, WorkspaceId = Guid.NewGuid() };
        var payment = new Payment { Id = paymentId, SubscriptionId = subscriptionId, Status = PaymentConstants.PaymentStatuses.Paid, Subscription = sub };

        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);

        // Second call inside retry block: GetWithSubscriptionAndPlanAsync
        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAndPlanAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);

        var request = new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123");
        var result = await _paymentService.HandleWebhookAsync(request);

        result.IsSuccess.Should().BeTrue();
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task HandleWebhookAsync_Paid_Should_ActivatePendingSubscription_And_CancelOldActive()
    {
        var paymentId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var plan = new Plan { Id = planId, BillingCycle = "monthly", CreditsPerCycle = 1000 };
        var pendingSub = new Subscription
        {
            Id = subscriptionId,
            UserId = userId,
            WorkspaceId = workspaceId,
            PlanId = planId,
            Status = SubscriptionConstants.SubscriptionStatuses.Pending,
            IsActive = false,
            CreditsRemaining = 0,
            Plan = plan
        };

        // payment with nav property populated (for GetWithSubscriptionAsync)
        var paymentNav = new Payment
        {
            Id = paymentId,
            SubscriptionId = subscriptionId,
            Status = PaymentConstants.PaymentStatuses.Pending,
            Subscription = pendingSub
        };

        // payment with full nav (for GetWithSubscriptionAndPlanAsync)
        var paymentFull = new Payment
        {
            Id = paymentId,
            SubscriptionId = subscriptionId,
            Status = PaymentConstants.PaymentStatuses.Pending,
            Subscription = pendingSub
        };

        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(paymentNav);
        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAndPlanAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(paymentFull);

        _mockSubRepo.Setup(r => r.DeactivateOtherActiveSubscriptionsAsync(userId, subscriptionId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var request = new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123");
        var result = await _paymentService.HandleWebhookAsync(request);

        result.IsSuccess.Should().BeTrue();
        paymentFull.Status.Should().Be(PaymentConstants.PaymentStatuses.Paid);
        pendingSub.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
        pendingSub.IsActive.Should().BeTrue();
        pendingSub.CreditsRemaining.Should().Be(1000);
        pendingSub.CurrentPeriodEnd.Should().BeAfter(DateTime.UtcNow.AddDays(27));
        pendingSub.CurrentPeriodEnd.Should().BeBefore(DateTime.UtcNow.AddDays(32));
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task HandleWebhookAsync_Failed_Should_MarkPaymentFailed()
    {
        var paymentId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var sub = new Subscription { Id = subscriptionId, WorkspaceId = workspaceId };
        var paymentNav = new Payment
        {
            Id = paymentId,
            SubscriptionId = subscriptionId,
            Status = PaymentConstants.PaymentStatuses.Pending,
            Subscription = sub
        };
        var paymentFull = new Payment
        {
            Id = paymentId,
            SubscriptionId = subscriptionId,
            Status = PaymentConstants.PaymentStatuses.Pending,
            Subscription = sub
        };

        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(paymentNav);
        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAndPlanAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(paymentFull);

        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest(paymentId.ToString(), "CANCELLED", "tx123"));

        result.IsSuccess.Should().BeTrue();
        paymentFull.Status.Should().Be(PaymentConstants.PaymentStatuses.Failed);
        paymentFull.FailureReason.Should().Be("CANCELLED");
    }

    [Fact]
    public async Task HandleWebhookAsync_Paid_SubscriptionNotFound_ShouldReturnFailure_WithoutSaving()
    {
        var paymentId = Guid.NewGuid();

        // Subscription nav property is null → service returns BillingSubscriptionNotFound immediately
        var payment = new Payment
        {
            Id = paymentId,
            SubscriptionId = Guid.NewGuid(),
            Status = PaymentConstants.PaymentStatuses.Pending,
            Subscription = null!
        };

        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);

        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task HandleWebhookAsync_Paid_SubNotPending_ShouldReturnInvalidState()
    {
        var paymentId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var plan = new Plan { Id = planId, BillingCycle = "monthly", CreditsPerCycle = 1000 };
        var activeSub = new Subscription
        {
            Id = subscriptionId,
            WorkspaceId = workspaceId,
            PlanId = planId,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            IsActive = true,
            Plan = plan
        };
        var paymentNav = new Payment
        {
            Id = paymentId,
            SubscriptionId = subscriptionId,
            Status = PaymentConstants.PaymentStatuses.Pending,
            Subscription = activeSub
        };
        var paymentFull = new Payment
        {
            Id = paymentId,
            SubscriptionId = subscriptionId,
            Status = PaymentConstants.PaymentStatuses.Pending,
            Subscription = activeSub
        };

        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(paymentNav);
        _mockPaymentRepo.Setup(r => r.GetWithSubscriptionAndPlanAsync(paymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(paymentFull);

        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidState); // was "NOT_SUPPORTED", actual: "INVALID_STATE"
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    // ─────────────────────────────────────────────
    //  CreatePaymentAsync Tests
    // ─────────────────────────────────────────────

    [Fact]
    public async Task CreatePaymentAsync_ValidRequest_ShouldCreatePayment()
    {
        var subId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var sub = new Subscription { Id = subId, PlanId = planId };
        var plan = new Plan { Id = planId, Price = 10m, Currency = "usd", BillingCycle = "monthly" };

        _mockSubRepo.Setup(r => r.GetByIdAsync(subId, default)).ReturnsAsync(sub);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, default)).ReturnsAsync(plan);

        var request = new CreatePaymentRequest(subId, Guid.NewGuid(), "card", "Stripe");
        var result = await _paymentService.CreatePaymentAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Amount.Should().Be(10m);
        result.Value.TaxAmount.Should().Be(1m);
        result.Value.TotalAmount.Should().Be(11m);
        _mockPaymentRepo.Verify(r => r.AddAsync(It.IsAny<Payment>(), default), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task CreatePaymentAsync_ZeroAmount_ShouldReturnFailure()
    {
        var subId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var sub = new Subscription { Id = subId, PlanId = planId };
        var plan = new Plan { Id = planId, Price = 0m, Currency = "usd", BillingCycle = "monthly" };

        _mockSubRepo.Setup(r => r.GetByIdAsync(subId, default)).ReturnsAsync(sub);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, default)).ReturnsAsync(plan);

        var request = new CreatePaymentRequest(subId, Guid.NewGuid(), "card", "Stripe");
        var result = await _paymentService.CreatePaymentAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInvalidAmount); // actual: "BILLING_INVALID_AMOUNT"
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task CreatePaymentAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        _mockSubRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _paymentService.CreatePaymentAsync(new CreatePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), "card", "Stripe"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetPaymentHistoryAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _paymentService.GetPaymentHistoryAsync(Guid.NewGuid(), new WarpTalk.BillingService.Application.DTOs.PaginationQuery(1, 20));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
    }
}
