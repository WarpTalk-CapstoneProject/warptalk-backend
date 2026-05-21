using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Linq.Expressions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Services;

public class CreditServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ILogger<CreditService>> _mockLogger;
    private readonly Mock<IBillingMessagePublisher> _mockMessagePublisher;
    private readonly Mock<IGenericRepository<Subscription>> _mockSubRepo;
    private readonly Mock<IGenericRepository<CreditTransaction>> _mockTxRepo;
    private readonly Mock<IGenericRepository<UsageRecord>> _mockUsageRepo;
    private readonly CreditService _creditService;

    public CreditServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<CreditService>>();
        _mockMessagePublisher = new Mock<IBillingMessagePublisher>();

        _mockSubRepo = new Mock<IGenericRepository<Subscription>>();
        _mockTxRepo = new Mock<IGenericRepository<CreditTransaction>>();
        _mockUsageRepo = new Mock<IGenericRepository<UsageRecord>>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.UsageRecordRepository).Returns(_mockUsageRepo.Object);

        _creditService = new CreditService(_mockUnitOfWork.Object, _mockLogger.Object, _mockMessagePublisher.Object);
    }

    [Fact]
    public async Task RecordUsageAsync_WithSufficientCredits_ShouldDeductCreditsAndPublishRealtime()
    {
        // Arrange
        var hostWorkspaceId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = hostWorkspaceId,
            IsActive = true,
            CreditsRemaining = 500, // Enough credits
            CreditsUsedThisCycle = 0
        };

        _mockSubRepo.Setup(repo => repo.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);

        var request = new RecordUsageRequest(
            HostWorkspaceId: hostWorkspaceId,
            UserId: Guid.NewGuid(),
            UsageType: "voice_clone",
            Unit: "minutes",
            Quantity: 5,
            CreditsConsumed: 100, // Cost is 100
            DurationSeconds: 300,
            TranslationRoomId: null,
            Details: null
        );

        // Act
        var result = await _creditService.RecordUsageAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.CurrentCredits.Should().Be(400); // 500 - 100

        // Verify Database Interactions
        _mockSubRepo.Verify(r => r.Update(It.Is<Subscription>(s => s.CreditsRemaining == 400)), Times.Once);
        _mockTxRepo.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUsageRepo.Verify(r => r.AddAsync(It.IsAny<UsageRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Verify Realtime Publish
        _mockMessagePublisher.Verify(p => p.PublishAsync(
            "warptalk:notifications:new",
            It.Is<WarpTalk.Shared.Models.RealtimeNotificationMessage>(m => 
                m.UserId == hostWorkspaceId.ToString() && m.Type == "billing.credits_updated"),
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task RecordUsageAsync_WithInsufficientCredits_ShouldReturnFailureErrorCode()
    {
        // Arrange
        var hostWorkspaceId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = hostWorkspaceId,
            IsActive = true,
            CreditsRemaining = 50 // Not enough credits
        };

        _mockSubRepo.Setup(repo => repo.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);

        var request = new RecordUsageRequest(
            HostWorkspaceId: hostWorkspaceId,
            UserId: Guid.NewGuid(),
            UsageType: "translation",
            Unit: "chars",
            Quantity: 5000,
            CreditsConsumed: 100, // Cost is 100
            DurationSeconds: null,
            TranslationRoomId: null,
            Details: null
        );

        // Act
        var result = await _creditService.RecordUsageAsync(request);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ErrorCodes.BillingInsufficientCredits);

        // Verify no database changes or publishes occurred
        _mockSubRepo.Verify(r => r.Update(It.IsAny<Subscription>()), Times.Never);
        _mockTxRepo.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockUsageRepo.Verify(r => r.AddAsync(It.IsAny<UsageRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockMessagePublisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
