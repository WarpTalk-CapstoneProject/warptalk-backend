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

public class SubscriptionPaymentEventHandlerTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ILogger<SubscriptionPaymentEventHandler>> _mockLogger;
    private readonly SubscriptionPaymentEventHandler _handler;

    public SubscriptionPaymentEventHandlerTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<SubscriptionPaymentEventHandler>>();

        // Setup mock repositories
        _mockUnitOfWork.Setup(u => u.Plans)
            .Returns(new Mock<IPlanRepository>().Object);
        var mockSubRepo = new Mock<ISubscriptionRepository>();
        mockSubRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<Subscription>());

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository)
            .Returns(mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository)
            .Returns(new Mock<ICreditTransactionRepository>().Object);

        _handler = new SubscriptionPaymentEventHandler(_mockUnitOfWork.Object, _mockLogger.Object);
    }

    private PaymentEventContext CreateContext(
        string paymentStatus = PaymentConstants.PaymentStatuses.Paid, 
        string planSlug = "enterprise", 
        string paymentType = PaymentConstants.PaymentTypes.SubscriptionRenewal,
        string billingCycle = "month",
        Subscription? existingSubscription = null)
    {
        var request = new StripePaymentEventRequest(
            StripeSessionId: "cs_test",
            PaymentIntentId: "pi_test",
            Amount: 1900000m,
            Currency: "vnd",
            UserIdStr: Guid.NewGuid().ToString(),
            WorkspaceIdStr: Guid.NewGuid().ToString(),
            PaymentType: paymentType,
            Status: "complete",
            FailureReason: "",
            InvoiceUrl: "",
            InvoicePdf: "",
            PlanSlug: planSlug,
            BillingCycle: billingCycle
        );

        return new PaymentEventContext(
            request: request,
            workspaceId: Guid.NewGuid(),
            userId: Guid.NewGuid(),
            providerTransactionId: Guid.NewGuid().ToString(),
            parsedPaymentStatus: paymentStatus,
            paymentId: Guid.NewGuid(),
            existingPayment: null,
            subscription: existingSubscription
        );
    }

    [Fact]
    public async Task HandleAsync_ShouldAccumulateDeadline_WhenSubscriptionIsActiveAndNotExpired()
    {
        // Arrange
        var existingEnd = DateTime.UtcNow.AddDays(10);
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            IsActive = true,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            CurrentPeriodEnd = existingEnd,
            CreditsRemaining = 100
        };

        var context = CreateContext(existingSubscription: subscription);
        var plan = new Plan { Id = Guid.NewGuid(), Slug = "enterprise", CreditsPerCycle = 700000 };

        _mockUnitOfWork.Setup(u => u.Plans.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _handler.HandleAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        context.SubscriptionChanged.Should().BeTrue();
        
        // Deadline should be accumulated (existingEnd + 1 month)
        context.Subscription.Should().NotBeNull();
        context.Subscription!.CurrentPeriodEnd.Should().BeCloseTo(existingEnd.AddMonths(1), TimeSpan.FromSeconds(5));
        context.Subscription.CreditsRemaining.Should().Be(700100);
        
        _mockUnitOfWork.Verify(u => u.CreditTransactionRepository.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ShouldResetDeadline_WhenSubscriptionIsExpired()
    {
        // Arrange
        var existingEnd = DateTime.UtcNow.AddDays(-5); // Expired 5 days ago
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            IsActive = true,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            CurrentPeriodEnd = existingEnd,
            CreditsRemaining = -50 // Negative balance (overage)
        };

        var context = CreateContext(existingSubscription: subscription);
        var plan = new Plan { Id = Guid.NewGuid(), Slug = "enterprise", CreditsPerCycle = 700000 };

        _mockUnitOfWork.Setup(u => u.Plans.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _handler.HandleAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        context.SubscriptionChanged.Should().BeTrue();
        
        // Deadline should be reset to Now + 1 month because it was expired
        context.Subscription.Should().NotBeNull();
        context.Subscription!.CurrentPeriodEnd.Should().BeCloseTo(DateTime.UtcNow.AddMonths(1), TimeSpan.FromSeconds(5));
        
        // Credits should still accumulate correctly (paying off debt)
        context.Subscription.CreditsRemaining.Should().Be(699950);
        
        _mockUnitOfWork.Verify(u => u.CreditTransactionRepository.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
