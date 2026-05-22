using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using System.Linq.Expressions;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class PaymentServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IGenericRepository<Payment>> _mockPaymentRepo;
    private readonly Mock<IGenericRepository<Subscription>> _mockSubRepo;
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly PaymentService _paymentService;

    public PaymentServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockPaymentRepo = new Mock<IGenericRepository<Payment>>();
        _mockSubRepo = new Mock<IGenericRepository<Subscription>>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();

        _mockUnitOfWork.Setup(u => u.PaymentRepository).Returns(_mockPaymentRepo.Object);
        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);

        var _mockCreditTxRepo = new Mock<IGenericRepository<CreditTransaction>>();
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockCreditTxRepo.Object);

        _paymentService = new PaymentService(_mockUnitOfWork.Object, new Mock<ILogger<PaymentService>>().Object);
    }

    [Fact]
    public async Task HandleWebhookAsync_Should_ActivatePendingSubscription_And_CancelOldActive()
    {
        var paymentId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        var payment = new Payment { Id = paymentId, SubscriptionId = subscriptionId, Status = "pending" };
        var plan = new Plan { Id = planId, BillingCycle = "yearly", CreditsPerCycle = 1000 };
        var pendingSub = new Subscription { Id = subscriptionId, PlanId = planId, Status = "pending", IsActive = false, CreditsRemaining = 0 };
        var oldSub = new Subscription { Id = Guid.NewGuid(), Status = "active", IsActive = true, AutoRenew = true };

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
    }
}
