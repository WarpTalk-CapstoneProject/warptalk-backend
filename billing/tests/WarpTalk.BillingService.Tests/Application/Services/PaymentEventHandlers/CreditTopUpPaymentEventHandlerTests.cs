using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services.PaymentEventHandlers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services.PaymentEventHandlers;

public class CreditTopUpPaymentEventHandlerTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ILogger<CreditTopUpPaymentEventHandler>> _mockLogger;
    private readonly CreditTopUpPaymentEventHandler _handler;

    public CreditTopUpPaymentEventHandlerTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<CreditTopUpPaymentEventHandler>>();

        // Setup mock repositories
        _mockUnitOfWork.Setup(u => u.Plans)
            .Returns(new Mock<IPlanRepository>().Object);
        _mockUnitOfWork.Setup(u => u.SubscriptionRepository)
            .Returns(new Mock<ISubscriptionRepository>().Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository)
            .Returns(new Mock<ICreditTransactionRepository>().Object);

        _handler = new CreditTopUpPaymentEventHandler(_mockUnitOfWork.Object, _mockLogger.Object);
    }

    private PaymentEventContext CreateContext(string paymentStatus = PaymentConstants.PaymentStatuses.Paid, string planSlug = "test-addon", string paymentType = PaymentConstants.PaymentTypes.CreditTopUp)
    {
        var request = new StripePaymentEventRequest(
            StripeSessionId: "cs_test",
            PaymentIntentId: "pi_test",
            Amount: 1000m,
            Currency: "usd",
            UserIdStr: Guid.NewGuid().ToString(),
            WorkspaceIdStr: Guid.NewGuid().ToString(),
            PaymentType: paymentType,
            Status: "complete",
            FailureReason: "",
            InvoiceUrl: "",
            InvoicePdf: "",
            PlanSlug: planSlug,
            BillingCycle: "month"
        );

        return new PaymentEventContext(
            request: request,
            workspaceId: Guid.NewGuid(),
            userId: Guid.NewGuid(),
            providerTransactionId: Guid.NewGuid().ToString(),
            parsedPaymentStatus: paymentStatus,
            paymentId: Guid.NewGuid(),
            existingPayment: null,
            subscription: null
        );
    }

    [Fact]
    public void CanHandle_ShouldReturnTrue_WhenPaymentTypeIsCreditTopUp()
    {
        // Arrange
        var context = CreateContext();

        // Act
        var result = _handler.CanHandle(context);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanHandle_ShouldReturnFalse_WhenPaymentTypeIsNotCreditTopUp()
    {
        // Arrange
        var context = CreateContext(paymentType: PaymentConstants.PaymentTypes.Subscription);

        // Act
        var result = _handler.CanHandle(context);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnFailure_WhenPlanNotFound()
    {
        // Arrange
        var context = CreateContext();
        
        _mockUnitOfWork.Setup(u => u.Plans.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Plan)null!);

        // Act
        var result = await _handler.HandleAsync(context);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingPlanNotFound);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnSuccess_WhenPaymentNotPaid()
    {
        // Arrange
        var context = CreateContext(paymentStatus: PaymentConstants.PaymentStatuses.Pending);
        var plan = new Plan { Id = Guid.NewGuid(), Slug = "test-addon" };

        _mockUnitOfWork.Setup(u => u.Plans.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _handler.HandleAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // Should not query for subscriptions or do anything else
        _mockUnitOfWork.Verify(u => u.SubscriptionRepository.FirstOrDefaultAsync(
            It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnFailure_WhenNoActiveSubscription()
    {
        // Arrange
        var context = CreateContext();
        var plan = new Plan { Id = Guid.NewGuid(), Slug = "test-addon" };

        _mockUnitOfWork.Setup(u => u.Plans.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription)null!);

        // Act
        var result = await _handler.HandleAsync(context);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
    }

    [Fact]
    public async Task HandleAsync_ShouldIncreaseCreditsAndAddTransaction_WhenValid()
    {
        // Arrange
        var context = CreateContext();
        var plan = new Plan { Id = Guid.NewGuid(), Slug = "test-addon", CreditsPerCycle = 1000 };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = context.WorkspaceId,
            IsActive = true,
            CreditsRemaining = 500,
            ServiceState = SubscriptionConstants.ServiceStates.Suspended,
            SuspendedReason = SubscriptionConstants.SuspendedReasons.OverageCap
        };

        _mockUnitOfWork.Setup(u => u.Plans.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);

        // Act
        var result = await _handler.HandleAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        subscription.CreditsRemaining.Should().Be(1500); // 500 + 1000
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy); // Resumed!
        subscription.SuspendedReason.Should().BeNull();
        
        context.SubscriptionChanged.Should().BeTrue();
        context.Subscription.Should().Be(subscription);

        _mockUnitOfWork.Verify(u => u.SubscriptionRepository.Update(subscription), Times.Once);
        _mockUnitOfWork.Verify(u => u.CreditTransactionRepository.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
