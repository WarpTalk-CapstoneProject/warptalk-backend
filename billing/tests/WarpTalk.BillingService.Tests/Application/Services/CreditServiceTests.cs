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

public class CreditServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IBillingMessagePublisher> _mockMessagePublisher;
    private readonly Mock<IGenericRepository<Subscription>> _mockSubRepo;
    private readonly Mock<IGenericRepository<CreditTransaction>> _mockTxRepo;
    private readonly Mock<IGenericRepository<UsageRecord>> _mockUsageRepo;
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly Mock<IGenericRepository<CreditBalanceSnapshot>> _mockSnapshotRepo;
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
        _mockSnapshotRepo = new Mock<IGenericRepository<CreditBalanceSnapshot>>();
        _mockCostCalculator = new Mock<IRealtimeCostCalculator>();
        _mockRedisStore = new Mock<IRedisBillingStore>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.UsageRecordRepository).Returns(_mockUsageRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditBalanceSnapshotRepository).Returns(_mockSnapshotRepo.Object);

        _creditService = new CreditService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<CreditService>>().Object,
            _mockMessagePublisher.Object,
            _mockCostCalculator.Object,
            _mockRedisStore.Object);
    }

    // ─────────────────────────────────────────────
    //  RecordUsageAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task RecordUsageAsync_WithSufficientCredits_ShouldDeductCreditsAndPublishRealtime()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId,
            PlanId = planId, IsActive = true, CreditsRemaining = 500, CreditsUsedThisCycle = 0
        };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = true, Name = "Pro" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "voice_clone", "minutes", 5, 100, 300, null, null);
        var result = await _creditService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CurrentCredits.Should().Be(400); // 500 - 100

        _mockSubRepo.Verify(r => r.Update(It.Is<Subscription>(s => s.CreditsRemaining == 400)), Times.Once);
        _mockTxRepo.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUsageRepo.Verify(r => r.AddAsync(It.IsAny<UsageRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockMessagePublisher.Verify(p => p.PublishAsync(
            "warptalk:notifications:new",
            It.Is<WarpTalk.Shared.Models.RealtimeNotificationMessage>(m =>
                m.UserId == hostWorkspaceId.ToString() && m.Type == "billing.credits_updated"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordUsageAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync((Subscription?)null);

        var request = new RecordUsageRequest(Guid.NewGuid(), Guid.NewGuid(), "translation_minutes", "minutes", 1, 15, null, null, null);
        var result = await _creditService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordUsageAsync_InsufficientCredits_ShouldReturnFailure()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId, PlanId = planId, IsActive = true, CreditsRemaining = 5 };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = true, Name = "Pro" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "translation_minutes", "minutes", 1, 100, null, null, null);
        var result = await _creditService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInsufficientCredits);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordUsageAsync_WithVoiceCloneOnFreePlan_ShouldReturnFeatureNotAvailable()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId, PlanId = planId, IsActive = true, CreditsRemaining = 500 };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = false, Name = "Free" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);

        var request = new RecordUsageRequest(hostWorkspaceId, Guid.NewGuid(), "voice_clone", "minutes", 5, 100, null, null, null);
        var result = await _creditService.RecordUsageAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FEATURE_NOT_AVAILABLE");
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─────────────────────────────────────────────
    //  ConsumeCreditsAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ConsumeCreditsAsync_WithSufficientCredits_ShouldDeductAndPublish()
    {
        var workspaceId = Guid.NewGuid();
        var sub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, IsActive = true, CreditsRemaining = 200, CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1) };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(sub);

        var request = new ConsumeCreditsRequest(50, "test_simulation", null);
        var result = await _creditService.ConsumeCreditsAsync(workspaceId, request);

        result.IsSuccess.Should().BeTrue();
        sub.CreditsRemaining.Should().Be(150);
        sub.CreditsUsedThisCycle.Should().Be(50);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumeCreditsAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync((Subscription?)null);

        var result = await _creditService.ConsumeCreditsAsync(Guid.NewGuid(), new ConsumeCreditsRequest(10, "test", null));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
    }

    [Fact]
    public async Task ConsumeCreditsAsync_InsufficientCredits_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        var sub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, IsActive = true, CreditsRemaining = 5, CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1) };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(sub);

        var result = await _creditService.ConsumeCreditsAsync(workspaceId, new ConsumeCreditsRequest(100, "test", null));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInsufficientCredits);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─────────────────────────────────────────────
    //  ReserveCreditsAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ReserveCreditsAsync_ShouldSetRedisReservationAndDeductMemory()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId, PlanId = planId, IsActive = true, CreditsRemaining = 500 };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = true, Name = "Pro" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);
        _mockCostCalculator.Setup(c => c.CalculateCreditCost(10, 0, 0, false, plan)).Returns(50);
        _mockTxRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<CreditTransaction, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync((CreditTransaction?)null);

        var request = new ReserveCreditsRequest(hostWorkspaceId, "idempotency_123", 10, 0, 0, false);
        var result = await _creditService.ReserveCreditsAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Amount.Should().Be(50);
        _mockRedisStore.Verify(r => r.SetReservationAsync(It.Is<RedisCreditReservation>(res => res.Amount == 50 && res.IdempotencyKey == "idempotency_123"), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReserveCreditsAsync_InsufficientCredits_ShouldReturnFailure()
    {
        var hostWorkspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = hostWorkspaceId, PlanId = planId, IsActive = true, CreditsRemaining = 10 };
        var plan = new Plan { Id = planId, VoiceCloneEnabled = true, Name = "Pro" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(planId, It.IsAny<CancellationToken>())).ReturnsAsync(plan);
        _mockCostCalculator.Setup(c => c.CalculateCreditCost(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), plan)).Returns(100);
        _mockTxRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<CreditTransaction, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync((CreditTransaction?)null);

        var result = await _creditService.ReserveCreditsAsync(new ReserveCreditsRequest(hostWorkspaceId, "key_x", 10, 0, 0, false));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInsufficientCredits);
        _mockRedisStore.Verify(r => r.SetReservationAsync(It.IsAny<RedisCreditReservation>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReserveCreditsAsync_IdempotentKey_ShouldReturnExistingReservation()
    {
        var existing = new CreditTransaction { Id = Guid.NewGuid(), SubscriptionId = Guid.NewGuid(), CorrelationId = "dup_key", Amount = 30, Type = "reserve" };
        _mockTxRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<CreditTransaction, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await _creditService.ReserveCreditsAsync(new ReserveCreditsRequest(Guid.NewGuid(), "dup_key", 5, 0, 0, false));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Amount.Should().Be(30);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─────────────────────────────────────────────
    //  ConfirmConsumeAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ConfirmConsumeAsync_ShouldCommitUsageAndClearReservation()
    {
        var workspaceId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        var subscription = new Subscription { Id = subId, WorkspaceId = workspaceId };
        var reservation = new RedisCreditReservation { SubscriptionId = subId, Amount = 50, IdempotencyKey = "idempotency_123" };

        _mockTxRepo.Setup(r => r.FirstOrDefaultAsync(It.Is<Expression<Func<CreditTransaction, bool>>>(e => true), It.IsAny<CancellationToken>())).ReturnsAsync((CreditTransaction?)null);
        _mockRedisStore.Setup(r => r.GetAndRemoveReservationAsync("idempotency_123", It.IsAny<CancellationToken>())).ReturnsAsync(reservation);
        _mockSubRepo.Setup(r => r.GetByIdAsync(subId, It.IsAny<CancellationToken>())).ReturnsAsync(subscription);

        var result = await _creditService.ConfirmConsumeAsync(workspaceId, "idempotency_123");

        result.IsSuccess.Should().BeTrue();
        _mockTxRepo.Verify(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmConsumeAsync_ReservationNotFound_ShouldReturnFailure()
    {
        _mockTxRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<CreditTransaction, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync((CreditTransaction?)null);
        _mockRedisStore.Setup(r => r.GetAndRemoveReservationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((RedisCreditReservation?)null);

        var result = await _creditService.ConfirmConsumeAsync(Guid.NewGuid(), "missing_key");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("RESERVATION_NOT_FOUND");
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─────────────────────────────────────────────
    //  RefundReservationAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task RefundReservationAsync_ShouldRestoreCreditsAndMarkRolledBack()
    {
        var workspaceId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        var subscription = new Subscription { Id = subId, WorkspaceId = workspaceId, CreditsRemaining = 450 };
        var reservation = new RedisCreditReservation { SubscriptionId = subId, Amount = 50, IdempotencyKey = "refund_key" };

        _mockTxRepo.Setup(r => r.FirstOrDefaultAsync(It.Is<Expression<Func<CreditTransaction, bool>>>(e => true), It.IsAny<CancellationToken>())).ReturnsAsync((CreditTransaction?)null);
        _mockRedisStore.Setup(r => r.GetAndRemoveReservationAsync("refund_key", It.IsAny<CancellationToken>())).ReturnsAsync(reservation);
        _mockSubRepo.Setup(r => r.GetByIdAsync(subId, It.IsAny<CancellationToken>())).ReturnsAsync(subscription);

        var result = await _creditService.RefundReservationAsync(workspaceId, "refund_key");

        result.IsSuccess.Should().BeTrue();
        subscription.CreditsRemaining.Should().Be(500); // 450 + 50
        _mockTxRepo.Verify(r => r.AddAsync(It.Is<CreditTransaction>(tx => tx.Type == "refund"), It.IsAny<CancellationToken>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefundReservationAsync_Idempotent_ShouldReturnSuccessWithoutProcessing()
    {
        var existingRefund = new CreditTransaction { Id = Guid.NewGuid(), Type = "refund", CorrelationId = "refund_key" };
        _mockTxRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<CreditTransaction, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(existingRefund);

        var result = await _creditService.RefundReservationAsync(Guid.NewGuid(), "refund_key");

        result.IsSuccess.Should().BeTrue();
        _mockRedisStore.Verify(r => r.GetAndRemoveReservationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─────────────────────────────────────────────
    //  AdjustCreditsAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task AdjustCreditsAsync_PositiveAmount_ShouldIncreaseBalance()
    {
        var subId = Guid.NewGuid();
        var sub = new Subscription { Id = subId, UserId = Guid.NewGuid(), CreditsRemaining = 100 };
        _mockSubRepo.Setup(r => r.GetByIdAsync(subId, It.IsAny<CancellationToken>())).ReturnsAsync(sub);

        var result = await _creditService.AdjustCreditsAsync(subId, 50, "Promo bonus", "admin-user-1");

        result.IsSuccess.Should().BeTrue();
        sub.CreditsRemaining.Should().Be(150);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdjustCreditsAsync_ZeroAmount_ShouldReturnFailure()
    {
        var result = await _creditService.AdjustCreditsAsync(Guid.NewGuid(), 0, "reason", "admin-1");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("INVALID_REQUEST");
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AdjustCreditsAsync_EmptyAdminUserId_ShouldReturnFailure()
    {
        var result = await _creditService.AdjustCreditsAsync(Guid.NewGuid(), 50, "reason", "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("INVALID_REQUEST");
    }

    [Fact]
    public async Task AdjustCreditsAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        _mockSubRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Subscription?)null);

        var result = await _creditService.AdjustCreditsAsync(Guid.NewGuid(), 100, "reason", "admin-1");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
    }

    // ─────────────────────────────────────────────
    //  GetBillingReportAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task GetBillingReportAsync_WithTransactions_ShouldReturnAccurateReport()
    {
        var workspaceId = Guid.NewGuid();
        var now = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var txs = new List<CreditTransaction>
        {
            new() { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Type = "top_up", Amount = 1000, Status = "committed", BalanceAfter = 1000, CreatedAt = now },
            new() { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Type = "consumption", Amount = -30, Status = "committed", BalanceAfter = 970, CreatedAt = now.AddHours(1) }
        };
        var usages = new List<UsageRecord>
        {
            new() { WorkspaceId = workspaceId, UsageType = "translation_minutes", CreditsConsumed = 15, RecordedAt = now.AddHours(1) },
            new() { WorkspaceId = workspaceId, UsageType = "translation_minutes", CreditsConsumed = 15, RecordedAt = now.AddHours(2) }
        };

        _mockTxRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<CreditTransaction, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(txs);
        _mockUsageRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<UsageRecord, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(usages);

        var result = await _creditService.GetBillingReportAsync(workspaceId, 2026, 6);

        result.IsSuccess.Should().BeTrue();
        result.Value!.TotalTopUpCredits.Should().Be(1000);
        result.Value.TotalConsumedCredits.Should().Be(30);
        result.Value.UsageBreakdown.Should().ContainSingle(u => u.UsageType == "translation_minutes" && u.TotalCreditsConsumed == 30);
    }

    [Fact]
    public async Task GetBillingReportAsync_NoTransactions_ShouldReturnZeroBalances()
    {
        var workspaceId = Guid.NewGuid();
        _mockTxRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<CreditTransaction, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<CreditTransaction>());
        _mockTxRepo.Setup(r => r.GetPagedAsync(It.IsAny<Expression<Func<CreditTransaction, bool>>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Func<IQueryable<CreditTransaction>, IOrderedQueryable<CreditTransaction>>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<CreditTransaction>());
        _mockUsageRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<UsageRecord, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<UsageRecord>());

        var result = await _creditService.GetBillingReportAsync(workspaceId, 2026, 6);

        result.IsSuccess.Should().BeTrue();
        result.Value!.StartingBalance.Should().Be(0);
        result.Value.EndingBalance.Should().Be(0);
        result.Value!.TotalConsumedCredits.Should().Be(0);
        result.Value.UsageBreakdown.Should().BeEmpty();
    }
}
