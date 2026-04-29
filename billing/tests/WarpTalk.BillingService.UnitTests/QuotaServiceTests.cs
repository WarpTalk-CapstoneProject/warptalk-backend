using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.UnitTests;

public class QuotaServiceTests
{
    private readonly Mock<IUsageQuotaRepository> _quotaRepoMock;
    private readonly Mock<IQuotaAuditLogRepository> _auditRepoMock;
    private readonly Mock<ISubscriptionPlanRepository> _planRepoMock;
    private readonly Mock<IUnitOfWork> _uowMock;
    private readonly Mock<ILogger<QuotaService>> _loggerMock;
    private readonly QuotaService _quotaService;

    public QuotaServiceTests()
    {
        _quotaRepoMock = new Mock<IUsageQuotaRepository>();
        _auditRepoMock = new Mock<IQuotaAuditLogRepository>();
        _planRepoMock = new Mock<ISubscriptionPlanRepository>();
        _uowMock = new Mock<IUnitOfWork>();
        _loggerMock = new Mock<ILogger<QuotaService>>();
        _quotaService = new QuotaService(
            _quotaRepoMock.Object,
            _auditRepoMock.Object,
            _planRepoMock.Object,
            _uowMock.Object,
            _loggerMock.Object
        );
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldReturnHasQuotaTrue_WhenMinutesAreAvailable()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var quota = new UsageQuota 
        { 
            WorkspaceId = workspaceId, 
            TotalAllocatedMinutes = 150, 
            ConsumedMinutes = 30,
            Plan = new SubscriptionPlan { Name = Domain.Enums.PlanType.Pro, MaxParticipants = 10 }
        };
        _quotaRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(quota);

        // Act
        var result = await _quotaService.CheckQuotaAsync(workspaceId);

        // Assert
        result.HasQuota.Should().BeTrue();
        result.RemainingMinutes.Should().Be(120); 
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldReturnHasQuotaFalse_WhenWorkspaceNotFound()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        _quotaRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync((UsageQuota)null);

        // Act
        var result = await _quotaService.CheckQuotaAsync(workspaceId);

        // Assert
        result.HasQuota.Should().BeFalse();
        result.RemainingMinutes.Should().Be(0);
    }

    [Fact]
    public async Task DeductQuotaAsync_ShouldDeductSuccessfully_WhenEnoughQuota()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var quota = new UsageQuota { WorkspaceId = workspaceId, TotalAllocatedMinutes = 100, ConsumedMinutes = 0 };
        _quotaRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(quota);

        var request = new QuotaDeductRequest(sessionId, 10, "Meeting");

        // Act
        var result = await _quotaService.DeductQuotaAsync(workspaceId, request);

        // Assert
        result.Success.Should().BeTrue();
        quota.ConsumedMinutes.Should().Be(10);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeductQuotaAsync_ShouldReturnSuccessFalse_WhenInsufficientQuota()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var quota = new UsageQuota { WorkspaceId = workspaceId, TotalAllocatedMinutes = 10, ConsumedMinutes = 5 };
        _quotaRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(quota);

        var request = new QuotaDeductRequest(sessionId, 10, "Meeting");

        // Act
        var result = await _quotaService.DeductQuotaAsync(workspaceId, request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("InsufficientQuota");
        quota.ConsumedMinutes.Should().Be(5); // Not changed
    }

    [Fact]
    public async Task DeductQuotaAsync_ShouldReturnConcurrencyConflict_WhenDbUpdateConcurrencyExceptionOccurs()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var quota = new UsageQuota { WorkspaceId = workspaceId, TotalAllocatedMinutes = 100, ConsumedMinutes = 0 };
        _quotaRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(quota);
        
        _uowMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException());

        var request = new QuotaDeductRequest(sessionId, 10, "Meeting");

        // Act
        var result = await _quotaService.DeductQuotaAsync(workspaceId, request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ConcurrencyConflict");
    }
}
