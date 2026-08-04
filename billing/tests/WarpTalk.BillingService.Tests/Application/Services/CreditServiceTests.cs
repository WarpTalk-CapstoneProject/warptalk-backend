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

public class CreditServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IBillingMessagePublisher> _mockMessagePublisher;
    private readonly Mock<IGenericRepository<Subscription>> _mockSubRepo;
    private readonly Mock<IGenericRepository<CreditTransaction>> _mockTxRepo;
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly Mock<IRedisBillingStore> _mockRedisStore;
    private readonly CreditService _creditService;

    public CreditServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockMessagePublisher = new Mock<IBillingMessagePublisher>();
        _mockSubRepo = new Mock<IGenericRepository<Subscription>>();
        _mockTxRepo = new Mock<IGenericRepository<CreditTransaction>>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();
        _mockRedisStore = new Mock<IRedisBillingStore>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);

        _creditService = new CreditService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<CreditService>>().Object,
            _mockMessagePublisher.Object,
            _mockRedisStore.Object,
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
    public async Task GetWorkspaceCreditsAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription?)null);

        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
    }

    [Fact]
    public async Task GetWorkspaceCreditsAsync_SubscriptionExists_ShouldReturnBalance()
    {
        var workspaceId = Guid.NewGuid();
        var sub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreditsRemaining = 1200, IsActive = true };
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sub);

        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CurrentCredits.Should().Be(1200);
    }

    [Fact]
    public async Task ConsumeCreditsAsync_InsufficientCredits_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        var sub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreditsRemaining = 50, IsActive = true, CurrentPeriodEnd = DateTime.UtcNow.AddDays(5) };
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sub);

        var request = new ConsumeCreditsRequest(100, "testing", null);
        var result = await _creditService.ConsumeCreditsAsync(workspaceId, request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInsufficientCredits);
    }

    [Fact]
    public async Task AdjustCreditsAsync_ShouldPersistAdministratorAsAuditActor()
    {
        var administratorId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            CreditsRemaining = 50,
            IsActive = true
        };
        CreditTransaction? persistedTransaction = null;
        _mockSubRepo
            .Setup(repository => repository.GetByIdAsync(subscription.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);
        _mockTxRepo
            .Setup(repository => repository.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Callback<CreditTransaction, CancellationToken>((transaction, _) => persistedTransaction = transaction)
            .Returns(Task.CompletedTask);
        _mockMessagePublisher
            .Setup(publisher => publisher.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<WarpTalk.Shared.Models.RealtimeNotificationMessage>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _creditService.AdjustCreditsAsync(
            subscription.Id,
            25,
            "Demo support grant",
            administratorId);

        result.IsSuccess.Should().BeTrue();
        persistedTransaction.Should().NotBeNull();
        persistedTransaction!.UserId.Should().Be(administratorId);
        persistedTransaction.ReferenceType.Should().Be("manual_adjustment");
        persistedTransaction.Description.Should().Be("Demo support grant");
        persistedTransaction.BalanceAfter.Should().Be(75);
        _mockMessagePublisher.Verify(publisher => publisher.PublishAsync(
            "warptalk:notifications:new",
            It.Is<WarpTalk.Shared.Models.RealtimeNotificationMessage>(notification =>
                notification.UserId == subscription.UserId.ToString()
                && notification.Type == "billing.credits_updated"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdjustCreditsAsync_ShouldRejectAdjustmentThatMakesBalanceNegative()
    {
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            CreditsRemaining = 10,
            IsActive = true
        };
        _mockSubRepo
            .Setup(repository => repository.GetByIdAsync(subscription.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);

        var result = await _creditService.AdjustCreditsAsync(
            subscription.Id,
            -11,
            "Correction",
            Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInsufficientCredits);
        _mockTxRepo.Verify(
            repository => repository.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ConfirmConsumeAsync_ShouldCommitDurableReservationWhenRedisEntryExpired()
    {
        var workspaceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            CreditsRemaining = 70,
            CreditsUsedThisCycle = 5,
            IsActive = true
        };
        var reservation = new CreditTransaction
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            CorrelationId = correlationId,
            Type = "consumption",
            Status = "reserved",
            Amount = -30,
            BalanceAfter = 70
        };
        _mockTxRepo
            .Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<CreditTransaction, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<CreditTransaction, bool>> predicate, CancellationToken _) =>
                predicate.Compile()(reservation) ? reservation : null);
        _mockSubRepo
            .Setup(repository => repository.GetByIdAsync(subscription.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);
        _mockRedisStore
            .Setup(store => store.GetAndRemoveReservationAsync(correlationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RedisCreditReservation?)null);

        var result = await _creditService.ConfirmConsumeAsync(workspaceId, correlationId);

        result.IsSuccess.Should().BeTrue();
        reservation.Status.Should().Be("committed");
        reservation.Description.Should().Be("AI Real-time consumption");
        subscription.CreditsUsedThisCycle.Should().Be(35);
        _mockTxRepo.Verify(repository => repository.Update(reservation), Times.Once);
    }

    [Fact]
    public async Task ConfirmConsumeAsync_ShouldNotFabricateTransactionWhenReservationIsMissing()
    {
        _mockTxRepo
            .Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<CreditTransaction, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreditTransaction?)null);

        var result = await _creditService.ConfirmConsumeAsync(
            Guid.NewGuid(),
            Guid.NewGuid().ToString());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("RESERVATION_NOT_FOUND");
        _mockTxRepo.Verify(
            repository => repository.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
