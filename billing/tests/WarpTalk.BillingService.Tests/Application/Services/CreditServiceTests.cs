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
    private readonly Mock<ISubscriptionRepository> _mockSubRepo;
    private readonly Mock<ICreditTransactionRepository> _mockTxRepo;
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly Mock<IUsageSettlementService> _mockSettlementService;
    private readonly CreditService _creditService;

    public CreditServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockSubRepo = new Mock<ISubscriptionRepository>();
        _mockTxRepo = new Mock<ICreditTransactionRepository>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();
        _mockSettlementService = new Mock<IUsageSettlementService>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.Plans).Returns(_mockPlanRepo.Object);

        _creditService = new CreditService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<CreditService>>().Object,
            _mockSettlementService.Object);
    }

    [Fact]
    public async Task GetWorkspaceCreditsAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        _mockSubRepo.Setup(r => r.GetActiveByWorkspaceIdAsync(workspaceId, true, false, It.IsAny<CancellationToken>()))
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
        _mockSubRepo.Setup(r => r.GetActiveByWorkspaceIdAsync(workspaceId, true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sub);

        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CurrentCredits.Should().Be(1200);
    }

    [Fact]
    public async Task ConsumeCreditsDirectlyAsync_InsufficientCredits_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        var sub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreditsRemaining = 50, IsActive = true, CurrentPeriodEnd = DateTime.UtcNow.AddDays(5) };
        _mockSubRepo.Setup(r => r.GetActiveByWorkspaceIdAsync(workspaceId, true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sub);
        _mockSettlementService
            .Setup(s => s.SettleUsageChargeAsync(It.IsAny<SettleUsageChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new SettleUsageChargeResult(false, null, null, sub.CreditsRemaining, "healthy", null)));

        var request = new ConsumeCreditsRequest(workspaceId, 100, "Manual", null);
        var result = await _creditService.ConsumeCreditsDirectlyAsync(workspaceId, request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingInsufficientCredits);
    }
}
