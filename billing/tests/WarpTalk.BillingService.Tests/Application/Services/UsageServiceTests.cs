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
using Microsoft.Extensions.Configuration;
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
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly Mock<IUsageSettlementService> _mockSettlementService;
    private readonly UsageService _usageService;

    public UsageServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockSubRepo = new Mock<ISubscriptionRepository>();
        _mockTxRepo = new Mock<ICreditTransactionRepository>();
        _mockUsageRepo = new Mock<IGenericRepository<UsageRecord>>();
        _mockPlanRepo = new Mock<IPlanRepository>();
        _mockConfig = new Mock<IConfiguration>();
        _mockSettlementService = new Mock<IUsageSettlementService>();

        _mockConfig.Setup(c => c["BillingRates:SttPerSecond"]).Returns("1.0");
        _mockConfig.Setup(c => c["BillingRates:TranslationPer100Chars"]).Returns("1.0");
        _mockConfig.Setup(c => c["BillingRates:StandardTtsPerSecond"]).Returns("1.0");
        _mockConfig.Setup(c => c["BillingRates:VoiceClonePerSecond"]).Returns("1.5");
        _mockConfig.Setup(c => c["BillingRates:AiAssistantInputPer1000Tokens"]).Returns("0.5");
        _mockConfig.Setup(c => c["BillingRates:AiAssistantOutputPer1000Tokens"]).Returns("2.0");

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.UsageRecordRepository).Returns(_mockUsageRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);

        _usageService = new UsageService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<UsageService>>().Object,
            null!,
            _mockSettlementService.Object);
    }

    [Fact]
    public void CalculateCreditCost_StandardUsage_ShouldCalculateCorrectly()
    {
        var rates = new ServiceRatesDto(
            SttPerSecond: 1.0,
            TranslationPer100Chars: 1.0,
            StandardTtsPerSecond: 1.0,
            VoiceClonePerSecond: 1.5,
            AiAssistantInputPer1000Tokens: 0.5,
            AiAssistantOutputPer1000Tokens: 2.0);

        // 60s STT (60 * 1) + 1000 chars translation (1000/100 * 1 = 10) + 1000ms standard TTS (1s * 1 = 1) = 71 credits
        var cost = CreditRatesHelper.CalculateCreditCost(new CreditCostRequest(
            AudioSeconds: 60,
            TokenCount: 1000,
            GpuInferenceMs: 1000,
            IsVoiceClone: false,
            Rates: rates));

        cost.Should().Be(71);
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
            Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId,
            PlanId = planId, IsActive = true, CreditsRemaining = 500, CreditsUsedThisCycle = 0, CurrentPeriodEnd = DateTime.UtcNow.AddDays(5)
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
