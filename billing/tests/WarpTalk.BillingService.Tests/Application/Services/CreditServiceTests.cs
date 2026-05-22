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
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class CreditServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IBillingMessagePublisher> _mockMessagePublisher;
    private readonly Mock<IGenericRepository<Subscription>> _mockSubRepo;
    private readonly Mock<IGenericRepository<CreditTransaction>> _mockTxRepo;
    private readonly Mock<IGenericRepository<UsageRecord>> _mockUsageRepo;
    private readonly CreditService _creditService;

    public CreditServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockMessagePublisher = new Mock<IBillingMessagePublisher>();

        _mockSubRepo = new Mock<IGenericRepository<Subscription>>();
        _mockTxRepo = new Mock<IGenericRepository<CreditTransaction>>();
        _mockUsageRepo = new Mock<IGenericRepository<UsageRecord>>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.UsageRecordRepository).Returns(_mockUsageRepo.Object);

        _creditService = new CreditService(_mockUnitOfWork.Object, new Mock<ILogger<CreditService>>().Object, _mockMessagePublisher.Object, new Mock<IRealtimeCostCalculator>().Object, new Mock<IRedisBillingStore>().Object);
    }

    [Fact]
    public async Task RecordUsageAsync_WithSufficientCredits_ShouldDeductCreditsAndPublishRealtime()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId, IsActive = true, CreditsRemaining = 500, CreditsUsedThisCycle = 0 };

        _mockSubRepo.Setup(repo => repo.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "voice_clone", "minutes", 5, 100, 300, null, null);
        var result = await _creditService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CurrentCredits.Should().Be(400); // 500 - 100

        _mockSubRepo.Verify(r => r.Update(It.Is<Subscription>(s => s.CreditsRemaining == 400)), Times.Once);
        _mockTxRepo.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUsageRepo.Verify(r => r.AddAsync(It.IsAny<UsageRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        
        _mockMessagePublisher.Verify(p => p.PublishAsync("warptalk:notifications:new", It.Is<WarpTalk.Shared.Models.RealtimeNotificationMessage>(m => m.UserId == hostWorkspaceId.ToString() && m.Type == "billing.credits_updated"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordUsageAsync_WithInsufficientCredits_ShouldReturnFailureErrorCode()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId, IsActive = true, CreditsRemaining = 50 };

        _mockSubRepo.Setup(repo => repo.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "translation", "chars", 5000, 100, null, null, null);
        var result = await _creditService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInsufficientCredits);

        _mockSubRepo.Verify(r => r.Update(It.IsAny<Subscription>()), Times.Never);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockMessagePublisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
