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
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly Mock<IRealtimeCostCalculator> _mockCostCalculator;
    private readonly Mock<IRedisBillingStore> _mockRedisStore;
    private readonly CreditService _creditService;

    public CreditServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockMessagePublisher = new Mock<IBillingMessagePublisher>();

        _mockSubRepo = new Mock<IGenericRepository<Subscription>>();
        _mockTxRepo = new Mock<IGenericRepository<CreditTransaction>>();
        _mockUsageRepo = new Mock<IGenericRepository<UsageRecord>>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();
        
        _mockCostCalculator = new Mock<IRealtimeCostCalculator>();
        _mockRedisStore = new Mock<IRedisBillingStore>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.UsageRecordRepository).Returns(_mockUsageRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);

        _creditService = new CreditService(
            _mockUnitOfWork.Object, 
            new Mock<ILogger<CreditService>>().Object, 
            _mockMessagePublisher.Object, 
            _mockCostCalculator.Object, 
            _mockRedisStore.Object);
    }

    [Fact]
    public async Task RecordUsageAsync_WithSufficientCredits_ShouldDeductCreditsAndPublishRealtime()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId, PlanId = planId, IsActive = true, CreditsRemaining = 500, CreditsUsedThisCycle = 0 };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = true, Name = "Pro" };

        _mockSubRepo.Setup(repo => repo.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(repo => repo.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "voice_clone", "minutes", 5, 100, 300, null, null);
        var result = await _creditService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CurrentCredits.Should().Be(400); // 500 - 100

        _mockSubRepo.Verify(r => r.Update(It.Is<Subscription>(s => s.CreditsRemaining == 400)), Times.Once);
        _mockTxRepo.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUsageRepo.Verify(r => r.AddAsync(It.IsAny<UsageRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        
        _mockMessagePublisher.Verify(p => p.PublishAsync("warptalk:notifications:new", It.Is<WarpTalk.Shared.Models.RealtimeNotificationMessage>(m => m.UserId == hostWorkspaceId.ToString() && m.Type == "billing.credits_updated"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordUsageAsync_WithVoiceCloneOnFreePlan_ShouldReturnFeatureNotAvailable()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId, PlanId = planId, IsActive = true, CreditsRemaining = 500 };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = false, Name = "Free" };

        _mockSubRepo.Setup(repo => repo.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(repo => repo.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "voice_clone", "minutes", 5, 100, null, null, null);
        var result = await _creditService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FEATURE_NOT_AVAILABLE");
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReserveCreditsAsync_ShouldSetRedisReservationAndDeductMemory()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId, PlanId = planId, IsActive = true, CreditsRemaining = 500 };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = true, Name = "Pro" };

        _mockSubRepo.Setup(repo => repo.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(repo => repo.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);
        _mockCostCalculator.Setup(c => c.CalculateCreditCost(10, 0, 0, false, plan)).Returns(50);

        var request = new ReserveCreditsRequest(hostWorkspaceId, "idempotency_123", 10, 0, 0, false);
        var result = await _creditService.ReserveCreditsAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Amount.Should().Be(50);

        _mockRedisStore.Verify(r => r.SetReservationAsync(It.Is<RedisCreditReservation>(res => res.Amount == 50 && res.IdempotencyKey == "idempotency_123"), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmConsumeAsync_ShouldCommitUsageAndClearReservation()
    {
        var workspaceId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        var subscription = new Subscription { Id = subId, WorkspaceId = workspaceId };
        var reservation = new RedisCreditReservation { SubscriptionId = subId, Amount = 50, IdempotencyKey = "idempotency_123" };

        _mockRedisStore.Setup(r => r.GetAndRemoveReservationAsync("idempotency_123", It.IsAny<CancellationToken>())).ReturnsAsync(reservation);
        _mockSubRepo.Setup(r => r.GetByIdAsync(subId, It.IsAny<CancellationToken>())).ReturnsAsync(subscription);

        var result = await _creditService.ConfirmConsumeAsync(workspaceId, "idempotency_123");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Amount.Should().Be(-50);

        _mockTxRepo.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

