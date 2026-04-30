using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    private const string VnPayHashSecret = "test-vnpay-hash-secret";

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
        _configMock.Setup(c => c["VNPay:HashSecret"]).Returns(VnPayHashSecret);

        // Mock IPayOsService
        var payOsServiceMock = new Mock<IPayOsService>();
        payOsServiceMock
            .Setup(p => p.CreateCheckoutLinkAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayOsCheckoutResponse
            {
                Code = "00",
                Desc = "Success",
                Data = new PayOsCheckoutData
                {
                    CheckoutUrl = "https://pay.payos.vn/web/test",
                    QrCode = ""
                }
            });

        _paymentService = new PaymentService(
            _transactionRepoMock.Object,
            _quotaRepoMock.Object,
            _auditRepoMock.Object,
            _planRepoMock.Object,
            _uowMock.Object,
            _loggerMock.Object,
            _configMock.Object,
            payOsServiceMock.Object
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

    [Fact]
    public async Task ProcessVnPayIpnAsync_ShouldUpdateTransactionAndTopUpQuota_WhenSignatureAndAmountValid()
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

        var query = CreateSignedVnPayIpn(orderCode, amountMinor: 10000000);

        // Act
        var response = await _paymentService.ProcessVnPayIpnAsync(query);

        // Assert
        response.RspCode.Should().Be("00");
        response.Message.Should().Be("Confirm Success");
        transaction.Status.Should().Be(TransactionStatus.Success);
        transaction.PayOsTransactionId.Should().Be("14226112");
        quota.TotalAllocatedMinutes.Should().Be(600);
        _transactionRepoMock.Verify(r => r.UpdateAsync(transaction, It.IsAny<CancellationToken>()), Times.Once);
        _auditRepoMock.Verify(r => r.AddAsync(It.Is<QuotaAuditLog>(log =>
            log.WorkspaceId == workspaceId &&
            log.Action == AuditAction.TopUp &&
            log.ReferenceId == $"VNPAY_{orderCode}"), It.IsAny<CancellationToken>()), Times.Once);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessVnPayIpnAsync_ShouldReturnInvalidSignature_WhenSecureHashDoesNotMatch()
    {
        // Arrange
        var query = CreateSignedVnPayIpn(orderCode: 12345, amountMinor: 10000000);
        query["vnp_SecureHash"] = "invalid";

        // Act
        var response = await _paymentService.ProcessVnPayIpnAsync(query);

        // Assert
        response.RspCode.Should().Be("97");
        _transactionRepoMock.Verify(r => r.GetByOrderCodeAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessVnPayIpnAsync_ShouldReturnInvalidAmount_WhenAmountDoesNotMatchTransaction()
    {
        // Arrange
        var orderCode = 12345L;
        var transaction = new Transaction
        {
            OrderCode = orderCode,
            Status = TransactionStatus.Pending,
            AmountVnd = 100000,
            PurchasedMinutes = 100
        };

        _transactionRepoMock.Setup(r => r.GetByOrderCodeAsync(orderCode, It.IsAny<CancellationToken>())).ReturnsAsync(transaction);

        var query = CreateSignedVnPayIpn(orderCode, amountMinor: 999999);

        // Act
        var response = await _paymentService.ProcessVnPayIpnAsync(query);

        // Assert
        response.RspCode.Should().Be("04");
        transaction.Status.Should().Be(TransactionStatus.Pending);
        _transactionRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessVnPayIpnAsync_ShouldReturnOrderAlreadyConfirmed_WhenTransactionIsNotPending()
    {
        // Arrange
        var orderCode = 12345L;
        var transaction = new Transaction
        {
            OrderCode = orderCode,
            Status = TransactionStatus.Success,
            AmountVnd = 100000,
            PurchasedMinutes = 100
        };

        _transactionRepoMock.Setup(r => r.GetByOrderCodeAsync(orderCode, It.IsAny<CancellationToken>())).ReturnsAsync(transaction);

        var query = CreateSignedVnPayIpn(orderCode, amountMinor: 10000000);

        // Act
        var response = await _paymentService.ProcessVnPayIpnAsync(query);

        // Assert
        response.RspCode.Should().Be("02");
        _transactionRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
        _quotaRepoMock.Verify(r => r.UpdateAsync(It.IsAny<UsageQuota>(), It.IsAny<CancellationToken>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessVnPayIpnAsync_ShouldReturnOrderNotFound_WhenTransactionDoesNotExist()
    {
        // Arrange
        var orderCode = 12345L;
        _transactionRepoMock.Setup(r => r.GetByOrderCodeAsync(orderCode, It.IsAny<CancellationToken>())).ReturnsAsync((Transaction?)null);

        var query = CreateSignedVnPayIpn(orderCode, amountMinor: 10000000);

        // Act
        var response = await _paymentService.ProcessVnPayIpnAsync(query);

        // Assert
        response.RspCode.Should().Be("01");
        _transactionRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
        _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Dictionary<string, string> CreateSignedVnPayIpn(long orderCode, long amountMinor)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = amountMinor.ToString(CultureInfo.InvariantCulture),
            ["vnp_BankCode"] = "NCB",
            ["vnp_BankTranNo"] = "VNP14226112",
            ["vnp_CardType"] = "ATM",
            ["vnp_OrderInfo"] = $"WarpTalk_Topup_{orderCode}",
            ["vnp_PayDate"] = "20260429120000",
            ["vnp_ResponseCode"] = "00",
            ["vnp_TmnCode"] = "DEMO0001",
            ["vnp_TransactionNo"] = "14226112",
            ["vnp_TransactionStatus"] = "00",
            ["vnp_TxnRef"] = orderCode.ToString(CultureInfo.InvariantCulture)
        };

        query["vnp_SecureHash"] = SignVnPayParameters(query);
        return query;
    }

    private static string SignVnPayParameters(IReadOnlyDictionary<string, string> query)
    {
        var hashData = string.Join(
            "&",
            query
                .Where(kvp => kvp.Key.StartsWith("vnp_", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(kvp.Key, "vnp_SecureHash", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(kvp.Key, "vnp_SecureHashType", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(kvp.Value))
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));

        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(VnPayHashSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(hashData))).ToLowerInvariant();
    }
}
