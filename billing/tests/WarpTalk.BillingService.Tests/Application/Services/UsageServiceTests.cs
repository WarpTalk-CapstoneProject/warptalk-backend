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
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;


namespace WarpTalk.BillingService.Tests.Application.Services;

public class UsageServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ISubscriptionRepository> _mockSubRepo;
    private readonly Mock<ICreditTransactionRepository> _mockTxRepo;
    private readonly Mock<IGenericRepository<UsageRecord>> _mockUsageRepo;
    private readonly Mock<IPlanRepository> _mockPlanRepo;
    private readonly Mock<IUsageSettlementService> _mockSettlementService;
    private readonly UsageService _usageService;

    public UsageServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockSubRepo = new Mock<ISubscriptionRepository>();
        _mockTxRepo = new Mock<ICreditTransactionRepository>();
        _mockUsageRepo = new Mock<IGenericRepository<UsageRecord>>();
        _mockPlanRepo = new Mock<IPlanRepository>();
        _mockSettlementService = new Mock<IUsageSettlementService>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.UsageRecordRepository).Returns(_mockUsageRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);

        _usageService = new UsageService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<UsageService>>().Object,
            _mockSettlementService.Object);
    }

    [Fact]
    public async Task RecordUsageAsync_SufficientCredits_ShouldDeductCredits()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = hostWorkspaceId,
            PlanId = planId,
            IsActive = true,
            CreditsRemaining = 500,
            CreditsUsedThisCycle = 0,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(5)
        };
        var plan = new Plan { Id = planId, Name = "Pro" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);
        _mockSettlementService
            .Setup(s => s.SettleUsageChargeAsync(It.IsAny<SettleUsageChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new SettleUsageChargeResult(true, Guid.NewGuid(), Guid.NewGuid(), 400, SubscriptionConstants.ServiceStates.Healthy, null)));

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "voice_clone", "minutes", 5, 100, 300, null, null);
        var result = await _usageService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CurrentCredits.Should().Be(400); // 500 - 100

        _mockSettlementService.Verify(
            s => s.SettleUsageChargeAsync(
                It.Is<SettleUsageChargeRequest>(r => r.CreditsConsumed == 100 && r.WorkspaceId == hostWorkspaceId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordUsageAsync_WithSegmentId_ShouldStoreSegmentId()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = hostWorkspaceId,
            PlanId = planId,
            IsActive = true,
            CreditsRemaining = 500,
            CreditsUsedThisCycle = 0,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(5)
        };
        var plan = new Plan { Id = planId, Name = "Pro" };
        var segmentId = Guid.NewGuid();

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);
        _mockSettlementService
            .Setup(s => s.SettleUsageChargeAsync(It.IsAny<SettleUsageChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new SettleUsageChargeResult(true, Guid.NewGuid(), Guid.NewGuid(), 400, SubscriptionConstants.ServiceStates.Healthy, null)));

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "voice_clone", "minutes", 5, 100, 300, null, segmentId, "Segment details");
        var result = await _usageService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeTrue();
        _mockSettlementService.Verify(
            s => s.SettleUsageChargeAsync(
                It.Is<SettleUsageChargeRequest>(r => r.TranscriptSegmentId == segmentId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
