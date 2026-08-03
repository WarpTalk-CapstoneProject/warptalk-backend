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
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly Mock<ICreditTransactionRepository> _mockCreditTxRepo;
    private readonly PaymentService _paymentService;

    public PaymentServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockPaymentRepo = new Mock<IPaymentRepository>();
        _mockSubRepo = new Mock<ISubscriptionRepository>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();
        _mockCreditTxRepo = new Mock<ICreditTransactionRepository>();

        _mockUnitOfWork.Setup(u => u.PaymentRepository).Returns(_mockPaymentRepo.Object);
        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.Plans).Returns(_mockPlanRepo.Object);
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
