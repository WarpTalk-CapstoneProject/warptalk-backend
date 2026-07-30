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
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Configuration;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class UsageServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IGenericRepository<Subscription>> _mockSubRepo;
    private readonly Mock<IGenericRepository<CreditTransaction>> _mockTxRepo;
    private readonly Mock<IGenericRepository<UsageRecord>> _mockUsageRepo;
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly UsageService _usageService;

    public UsageServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockSubRepo = new Mock<IGenericRepository<Subscription>>();
        _mockTxRepo = new Mock<IGenericRepository<CreditTransaction>>();
        _mockUsageRepo = new Mock<IGenericRepository<UsageRecord>>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();
        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.UsageRecordRepository).Returns(_mockUsageRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);

        _usageService = new UsageService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<UsageService>>().Object,
            Options.Create(new BillingRatesOptions
            {
                SttPerMinute = 15.0,
                TranslationPerMinute = 15.0,
                StandardTtsPerMinute = 15.0,
                VoiceClonePerMinute = 40.0,
                AiSummaryPerRequest = 5.0,
                AiChatPerRequest = 2.0
            }));
    }

    [Fact]
    public void CalculateCreditCost_StandardUsage_ShouldCalculateCorrectly()
    {
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Pro" };
        
        // 60s STT + 60s Translation + 60s Standard TTS (15 + 15 + 15 = 45 credits)
        var cost = _usageService.CalculateCreditCost(60, 1000, 1000, false, plan);

        cost.Should().Be(45);
    }

    [Fact]
    public async Task RecordUsageAsync_SufficientCredits_ShouldDeductCredits()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId,
            PlanId = planId, IsActive = true, CreditsRemaining = 500, CreditsUsedThisCycle = 0, CurrentPeriodEnd = DateTime.UtcNow.AddDays(5)
        };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = true, Name = "Pro" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "voice_clone", "minutes", 5, 100, 300, null, null);
        var result = await _usageService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CurrentCredits.Should().Be(400); // 500 - 100

        _mockSubRepo.Verify(r => r.Update(It.Is<Subscription>(s => s.CreditsRemaining == 400)), Times.Once);
        _mockTxRepo.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUsageRepo.Verify(r => r.AddAsync(It.IsAny<UsageRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordUsageAsync_WithSegmentId_ShouldStoreSegmentId()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId,
            PlanId = planId, IsActive = true, CreditsRemaining = 500, CreditsUsedThisCycle = 0, CurrentPeriodEnd = DateTime.UtcNow.AddDays(5)
        };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = true, Name = "Pro" };
        var segmentId = Guid.NewGuid();

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "voice_clone", "minutes", 5, 100, 300, null, segmentId, "Segment details");
        var result = await _usageService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeTrue();
        _mockUsageRepo.Verify(r => r.AddAsync(It.Is<UsageRecord>(u => u.SegmentId == segmentId), It.IsAny<CancellationToken>()), Times.Once);
    }
}
