using System;
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
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace WarpTalk.BillingService.UnitTests;

public class PaymentServiceTests
{
    private readonly Mock<ITransactionRepository> _transactionRepoMock;
    private readonly Mock<IUsageQuotaRepository> _quotaRepoMock;
    private readonly Mock<IQuotaAuditLogRepository> _auditRepoMock;
    private readonly Mock<ISubscriptionPlanRepository> _planRepoMock;
    private readonly Mock<IUnitOfWork> _uowMock;
    private readonly Mock<ILogger<PaymentService>> _loggerMock;
    private readonly Mock<IConfiguration> _configMock;
    private readonly PaymentService _paymentService;

    public PaymentServiceTests()
    {
        _transactionRepoMock = new Mock<ITransactionRepository>();
        _quotaRepoMock = new Mock<IUsageQuotaRepository>();
        _auditRepoMock = new Mock<IQuotaAuditLogRepository>();
        _planRepoMock = new Mock<ISubscriptionPlanRepository>();
        _uowMock = new Mock<IUnitOfWork>();
        _loggerMock = new Mock<ILogger<PaymentService>>();
        _configMock = new Mock<IConfiguration>();

        // Mock configuration for PayOS in unit-test mode (no real signature material).
        _configMock.Setup(c => c["PayOS:ChecksumKey"]).Returns(string.Empty);
        _configMock.Setup(c => c["ASPNETCORE_ENVIRONMENT"]).Returns("Development");
        _configMock.Setup(c => c["Security:AllowInsecureWebhookSignatureInDevelopment"]).Returns("true");

        _paymentService = new PaymentService(
            _transactionRepoMock.Object,
            _quotaRepoMock.Object,
            _auditRepoMock.Object,
            _planRepoMock.Object,
            _uowMock.Object,
            _loggerMock.Object,
            _configMock.Object
        );
    }

    [Fact]
    public async Task ProcessPayOsWebhookAsync_ShouldUpdateTransactionAndTopUpQuota_WhenSuccess()
    {
        // Arrange
        var orderCode = 12345L;
        var workspaceId = Guid.NewGuid();
        var transaction = new Transaction 
        { 
            OrderCode = orderCode, 
            WorkspaceId = workspaceId, 
            Status = TransactionStatus.Pending,
            AmountVnd = 100000,
            PurchasedMinutes = 100
        };
        var quota = new UsageQuota { WorkspaceId = workspaceId, TotalAllocatedMinutes = 500, ConsumedMinutes = 100 };

        _transactionRepoMock.Setup(r => r.GetByOrderCodeAsync(orderCode, It.IsAny<CancellationToken>())).ReturnsAsync(transaction);
        _quotaRepoMock.Setup(r => r.GetByWorkspaceIdAsync(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(quota);

        var payload = new PayOsWebhookPayload
        {
            Code = "00",
            Data = new PayOsWebhookData { OrderCode = orderCode }
        };

        // Act
        await _paymentService.ProcessPayOsWebhookAsync(payload);

        // Assert
        transaction.Status.Should().Be(TransactionStatus.Success);
        quota.TotalAllocatedMinutes.Should().Be(600); // 500 + 100
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPayOsWebhookAsync_ShouldNotUpdate_WhenTransactionAlreadySuccess()
    {
        // Arrange
        var orderCode = 12345L;
        var transaction = new Transaction { OrderCode = orderCode, Status = TransactionStatus.Success };
        
        _transactionRepoMock.Setup(r => r.GetByOrderCodeAsync(orderCode, It.IsAny<CancellationToken>())).ReturnsAsync(transaction);

        var payload = new PayOsWebhookPayload
        {
            Code = "00",
            Data = new PayOsWebhookData { OrderCode = orderCode }
        };

        // Act
        await _paymentService.ProcessPayOsWebhookAsync(payload);

        // Assert
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPayOsWebhookAsync_ShouldMarkFailed_WhenPayloadCodeIsNot00()
    {
        // Arrange
        var orderCode = 12345L;
        var transaction = new Transaction { OrderCode = orderCode, Status = TransactionStatus.Pending };
        
        _transactionRepoMock.Setup(r => r.GetByOrderCodeAsync(orderCode, It.IsAny<CancellationToken>())).ReturnsAsync(transaction);

        var payload = new PayOsWebhookPayload
        {
            Code = "01", // Failure
            Data = new PayOsWebhookData { OrderCode = orderCode }
        };

        // Act
        await _paymentService.ProcessPayOsWebhookAsync(payload);

        // Assert
        transaction.Status.Should().Be(TransactionStatus.Failed);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
