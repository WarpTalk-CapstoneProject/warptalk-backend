using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests;

public class QuotaServiceTests
{
    private readonly Mock<IUsageQuotaRepository> _quotaRepoMock;
    private readonly Mock<IQuotaAuditLogRepository> _auditRepoMock;
    private readonly Mock<ISubscriptionPlanRepository> _planRepoMock;
    private readonly Mock<IUnitOfWork> _uowMock;
    private readonly Mock<ILogger<QuotaService>> _loggerMock;
    private readonly QuotaService _service;

    public QuotaServiceTests()
    {
        _quotaRepoMock = new Mock<IUsageQuotaRepository>();
        _auditRepoMock = new Mock<IQuotaAuditLogRepository>();
        _planRepoMock = new Mock<ISubscriptionPlanRepository>();
        _uowMock = new Mock<IUnitOfWork>();
        _loggerMock = new Mock<ILogger<QuotaService>>();

        _service = new QuotaService(
            _quotaRepoMock.Object,
            _auditRepoMock.Object,
            _planRepoMock.Object,
            _uowMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task DeductQuotaAsync_ShouldSucceed_WhenBalanceIsEnough()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var request = new QuotaDeductRequest(sessionId, 10m, "Test Source");
        
        var quota = new UsageQuota
        {
            WorkspaceId = workspaceId,
            TotalAllocatedMinutes = 100m,
            ConsumedMinutes = 20m
        };

        _quotaRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(quota);

        // Act
        var result = await _service.DeductQuotaAsync(workspaceId, request, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.RemainingMinutes.Should().Be(70m); // 100 - (20 + 10)
        _quotaRepoMock.Verify(r => r.UpdateAsync(It.Is<UsageQuota>(q => q.ConsumedMinutes == 30m), It.IsAny<CancellationToken>()), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeductQuotaAsync_ShouldFail_WhenBalanceIsInsufficient()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var request = new QuotaDeductRequest(Guid.NewGuid(), 50m, "Test Source");
        
        var quota = new UsageQuota
        {
            WorkspaceId = workspaceId,
            TotalAllocatedMinutes = 100m,
            ConsumedMinutes = 80m // Only 20 left
        };

        _quotaRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(quota);

        // Act
        var result = await _service.DeductQuotaAsync(workspaceId, request, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("InsufficientQuota");
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefundQuotaAsync_ShouldSucceed_AndIncreaseBalance()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var request = new QuotaRefundRequest { SessionId = sessionId, RefundedMinutes = 15m, Reason = "Correction" };

        var quota = new UsageQuota
        {
            WorkspaceId = workspaceId,
            TotalAllocatedMinutes = 100m,
            ConsumedMinutes = 40m
        };

        _quotaRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(quota);

        // Act
        var result = await _service.RefundQuotaAsync(workspaceId, request, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.RemainingMinutes.Should().Be(75m); // 100 - (40 - 15)
        _quotaRepoMock.Verify(r => r.UpdateAsync(It.Is<UsageQuota>(q => q.ConsumedMinutes == 25m), It.IsAny<CancellationToken>()), Times.Once);
    }
}
