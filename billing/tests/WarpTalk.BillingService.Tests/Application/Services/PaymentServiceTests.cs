using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class PaymentAndLedgerServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IGenericRepository<Payment>> _mockPaymentRepo;
    private readonly Mock<IGenericRepository<Subscription>> _mockSubRepo;
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly Mock<IGenericRepository<CreditTransaction>> _mockCreditTxRepo;
    private readonly PaymentAndLedgerService _paymentService;

    public PaymentAndLedgerServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockPaymentRepo = new Mock<IGenericRepository<Payment>>();
        _mockSubRepo = new Mock<IGenericRepository<Subscription>>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();
        _mockCreditTxRepo = new Mock<IGenericRepository<CreditTransaction>>();

        _mockUnitOfWork.Setup(u => u.PaymentRepository).Returns(_mockPaymentRepo.Object);
        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockCreditTxRepo.Object);

        _paymentService = new PaymentAndLedgerService(_mockUnitOfWork.Object, new Mock<ILogger<PaymentAndLedgerService>>().Object);
    }

    // ─────────────────────────────────────────────
    //  HandleWebhookAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task HandleWebhookAsync_Paid_Should_ActivatePendingSubscription_And_CancelOldActive()
    {
        var paymentId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var payment = new Payment { Id = paymentId, SubscriptionId = subscriptionId, Status = "pending" };
        var plan = new Plan { Id = planId, BillingCycle = "yearly", CreditsPerCycle = 1000 };
        var pendingSub = new Subscription { Id = subscriptionId, UserId = userId, PlanId = planId, Status = "pending", IsActive = false, CreditsRemaining = 0 };
        var oldSub = new Subscription { Id = Guid.NewGuid(), UserId = userId, Status = "active", IsActive = true, AutoRenew = true };

        _mockPaymentRepo.Setup(r => r.GetByIdAsync(paymentId, default)).ReturnsAsync(payment);
        _mockSubRepo.Setup(r => r.GetByIdAsync(subscriptionId, default)).ReturnsAsync(pendingSub);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.GetPagedAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), 0, 10, null, default)).ReturnsAsync(new List<Subscription> { oldSub });

        var request = new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123");
        var result = await _paymentService.HandleWebhookAsync(request);

        result.IsSuccess.Should().BeTrue();
        payment.Status.Should().Be("paid");
        oldSub.Status.Should().Be("cancelled");
        oldSub.AutoRenew.Should().BeFalse();
        pendingSub.Status.Should().Be("active");
        pendingSub.IsActive.Should().BeTrue();
        pendingSub.CreditsRemaining.Should().Be(1000);
        pendingSub.CurrentPeriodEnd.Should().BeAfter(DateTime.UtcNow.AddMonths(11));
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task HandleWebhookAsync_Idempotent_Should_Ignore_If_AlreadyPaid()
    {
        var paymentId = Guid.NewGuid();
        var payment = new Payment { Id = paymentId, Status = "paid" };

        _mockPaymentRepo.Setup(r => r.GetByIdAsync(paymentId, default)).ReturnsAsync(payment);

        var request = new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123");
        var result = await _paymentService.HandleWebhookAsync(request);

        result.IsSuccess.Should().BeTrue();
        _mockSubRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task HandleWebhookAsync_PaymentNotFound_ShouldReturnFailure()
    {
        var paymentId = Guid.NewGuid();
        _mockPaymentRepo.Setup(r => r.GetByIdAsync(paymentId, default)).ReturnsAsync((Payment?)null);

        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task HandleWebhookAsync_InvalidOrderCode_ShouldReturnFailure()
    {
        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest("not-a-guid", "PAID", "tx123"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("INVALID_REQUEST");
        _mockPaymentRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleWebhookAsync_Failed_Should_MarkPaymentFailed()
    {
        var paymentId = Guid.NewGuid();
        var payment = new Payment { Id = paymentId, Status = "pending" };

        _mockPaymentRepo.Setup(r => r.GetByIdAsync(paymentId, default)).ReturnsAsync(payment);

        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest(paymentId.ToString(), "CANCELLED", "tx123"));

        result.IsSuccess.Should().BeTrue();
        payment.Status.Should().Be("failed");
        payment.FailureReason.Should().Be("CANCELLED");
        _mockSubRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleWebhookAsync_Paid_SubscriptionNotFound_ShouldReturnFailure_WithoutSaving()
    {
        // EDGE CASE: The critical bug that was fixed — sub is null after PAID,
        // previously would have marked payment paid without activating subscription.
        var paymentId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var payment = new Payment { Id = paymentId, SubscriptionId = subscriptionId, Status = "pending" };

        _mockPaymentRepo.Setup(r => r.GetByIdAsync(paymentId, default)).ReturnsAsync(payment);
        _mockSubRepo.Setup(r => r.GetByIdAsync(subscriptionId, default)).ReturnsAsync((Subscription?)null);

        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
        payment.Status.Should().Be("pending"); // Must NOT be changed to "paid"
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task HandleWebhookAsync_Paid_SubNotPending_ShouldReturnNotSupported()
    {
        var paymentId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        var payment = new Payment { Id = paymentId, SubscriptionId = subscriptionId, Status = "pending" };
        var plan = new Plan { Id = planId, BillingCycle = "monthly", CreditsPerCycle = 1000 };
        var activeSub = new Subscription { Id = subscriptionId, PlanId = planId, Status = "active", IsActive = true };

        _mockPaymentRepo.Setup(r => r.GetByIdAsync(paymentId, default)).ReturnsAsync(payment);
        _mockSubRepo.Setup(r => r.GetByIdAsync(subscriptionId, default)).ReturnsAsync(activeSub);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, default)).ReturnsAsync(plan);

        var result = await _paymentService.HandleWebhookAsync(new PaymentWebhookRequest(paymentId.ToString(), "PAID", "tx123"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_SUPPORTED");
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    // ─────────────────────────────────────────────
    //  CreatePaymentAsync
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
        result.ErrorCode.Should().Be("INVALID_REQUEST");
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task CreatePaymentAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        _mockSubRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _paymentService.CreatePaymentAsync(new CreatePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), "card", "Stripe"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    // ─────────────────────────────────────────────
    //  GetPaymentHistoryAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task GetPaymentHistoryAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _paymentService.GetPaymentHistoryAsync(Guid.NewGuid(), 1, 20);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
    }
}
