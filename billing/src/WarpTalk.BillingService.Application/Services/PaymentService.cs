using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public class PaymentService : IPaymentService
{
    private const string VnPaySuccessCode = "00";
    private const string VnPayOrderNotFoundCode = "01";
    private const string VnPayOrderAlreadyConfirmedCode = "02";
    private const string VnPayInvalidAmountCode = "04";
    private const string VnPayInvalidSignatureCode = "97";
    private const string VnPayUnknownErrorCode = "99";

    private readonly ITransactionRepository _transactionRepository;
    private readonly IUsageQuotaRepository _quotaRepository;
    private readonly IQuotaAuditLogRepository _auditLogRepository;
    private readonly ISubscriptionPlanRepository _planRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PaymentService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPayOsService _payOsService;

    public PaymentService(
        ITransactionRepository transactionRepository,
        IUsageQuotaRepository quotaRepository,
        IQuotaAuditLogRepository auditLogRepository,
        ISubscriptionPlanRepository planRepository,
        IUnitOfWork unitOfWork,
        ILogger<PaymentService> logger,
        IConfiguration configuration,
        IPayOsService payOsService)
    {
        _transactionRepository = transactionRepository;
        _quotaRepository = quotaRepository;
        _auditLogRepository = auditLogRepository;
        _planRepository = planRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _configuration = configuration;
        _payOsService = payOsService;
    }

    public async Task<PaymentLinkResponse> CreatePaymentLinkAsync(Guid workspaceId, CreatePaymentLinkRequest request, CancellationToken cancellationToken = default)
    {
        decimal amount = 0;
        decimal minutes = 0;
        string description = "";

        if (request.PlanId.HasValue)
        {
            var plan = await _planRepository.GetByIdAsync(request.PlanId.Value, cancellationToken);
            if (plan == null || !plan.IsActive)
            {
                throw new InvalidOperationException("Subscription plan not found or inactive.");
            }

            amount = plan.PriceVnd;
            minutes = plan.BaseQuotaMinutes;
            description = $"Upgrade to {plan.Name} Plan";
        }
        else if (request.TopUpMinutes.HasValue)
        {
            // Simple pricing for top-up: 500 VND per minute (example)
            minutes = request.TopUpMinutes.Value;
            amount = minutes * 500;
            description = $"Top-up {minutes} minutes";
        }
        else
        {
            throw new InvalidOperationException("Must provide either PlanId or TopUpMinutes.");
        }

        // Generate a unique order code (numeric for PayOS)
        var randomSuffix = RandomNumberGenerator.GetInt32(100, 1000);
        long orderCode = long.Parse($"{DateTime.UtcNow:yyMMddHHmmss}{randomSuffix}", CultureInfo.InvariantCulture);

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            OrderCode = orderCode,
            AmountVnd = amount,
            PurchasedMinutes = minutes,
            Status = TransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        await _transactionRepository.AddAsync(transaction, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Create real PayOS checkout link
        var returnUrl = _configuration["Webhook:ReturnUrl"] ?? "http://localhost:3000/checkout/result";
        var cancelUrl = _configuration["Webhook:CancelUrl"] ?? "http://localhost:3000/checkout/cancel";
        
        var payOsResponse = await _payOsService.CreateCheckoutLinkAsync(
            orderCode,
            (long)amount,
            description,
            returnUrl,
            cancelUrl,
            cancellationToken);

        _logger.LogInformation("Created payment link for Workspace {WorkspaceId}, OrderCode {OrderCode}, Amount {Amount}", 
            workspaceId, orderCode, amount);

        return new PaymentLinkResponse
        {
            CheckoutUrl = payOsResponse.Data.CheckoutUrl,
            OrderCode = orderCode,
            Status = "Pending"
        };
    }

    public async Task ProcessPayOsWebhookAsync(PayOsWebhookPayload payload, CancellationToken cancellationToken = default)
    {
        // 1. Verify Signature (Implement hash validation)
        if (!VerifySignature(payload))
        {
            _logger.LogWarning("Invalid PayOS Webhook signature for OrderCode: {OrderCode}", payload.Data?.OrderCode);
            throw new UnauthorizedAccessException("Invalid signature.");
        }

        var orderCode = payload.Data?.OrderCode ?? 0;

        var isSuccess = payload.Code == "00";

        _logger.LogInformation("Processing verified webhook for OrderCode: {OrderCode}, Success: {Success}", orderCode, isSuccess);

        if (orderCode == 0) return;

        var transaction = await _transactionRepository.GetByOrderCodeAsync(orderCode, cancellationToken);
        if (transaction == null)
        {
            _logger.LogWarning("Transaction with OrderCode {OrderCode} not found.", orderCode);
            return;
        }

        if (transaction.Status == TransactionStatus.Success)
        {
            _logger.LogInformation("Transaction {OrderCode} already processed successfully. Idempotent return.", orderCode);
            return;
        }

        if (isSuccess)
        {
            transaction.Status = TransactionStatus.Success;
            transaction.PayOsTransactionId = payload.Data?.Reference;
            transaction.CompletedAt = DateTime.UtcNow;

            var quota = await _quotaRepository.GetByWorkspaceIdAsync(transaction.WorkspaceId, cancellationToken);
            if (quota != null)
            {
                // Top up the total allocated minutes
                quota.TotalAllocatedMinutes += transaction.PurchasedMinutes;
                await _quotaRepository.UpdateAsync(quota, cancellationToken);

                var auditLog = new QuotaAuditLog
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = transaction.WorkspaceId,
                    Action = AuditAction.TopUp,
                    Amount = transaction.PurchasedMinutes,
                    BalanceAfter = quota.TotalAllocatedMinutes - quota.ConsumedMinutes,
                    ReferenceId = $"TOPUP_{orderCode}",
                    Description = $"Top up {transaction.PurchasedMinutes} mins from order {orderCode}",
                    CreatedAt = DateTime.UtcNow
                };
                await _auditLogRepository.AddAsync(auditLog, cancellationToken);
            }
            else
            {
                _logger.LogWarning("Workspace {WorkspaceId} not found while topping up quota for OrderCode {OrderCode}.", transaction.WorkspaceId, orderCode);
            }
        }
        else
        {
            transaction.Status = TransactionStatus.Failed;
            transaction.CompletedAt = DateTime.UtcNow;
        }

        await _transactionRepository.UpdateAsync(transaction, cancellationToken);
        
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Successfully updated transaction {OrderCode} to {Status}", orderCode, transaction.Status);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to update transaction {OrderCode} due to DB error", orderCode);
            throw;
        }
    }

    public async Task<VnPayIpnResponse> ProcessVnPayIpnAsync(IReadOnlyDictionary<string, string> queryParameters, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!VerifyVnPaySignature(queryParameters))
            {
                _logger.LogWarning("Invalid VNPAY IPN signature for TxnRef: {TxnRef}", GetQueryValue(queryParameters, "vnp_TxnRef"));
                return new VnPayIpnResponse(VnPayInvalidSignatureCode, "Invalid signature");
            }

            if (!long.TryParse(GetQueryValue(queryParameters, "vnp_TxnRef"), NumberStyles.None, CultureInfo.InvariantCulture, out var orderCode))
            {
                _logger.LogWarning("Invalid VNPAY IPN TxnRef format.");
                return new VnPayIpnResponse(VnPayOrderNotFoundCode, "Order not found");
            }

            var transaction = await _transactionRepository.GetByOrderCodeAsync(orderCode, cancellationToken);
            if (transaction == null)
            {
                _logger.LogWarning("VNPAY IPN transaction with OrderCode {OrderCode} not found.", orderCode);
                return new VnPayIpnResponse(VnPayOrderNotFoundCode, "Order not found");
            }

            if (!long.TryParse(GetQueryValue(queryParameters, "vnp_Amount"), NumberStyles.None, CultureInfo.InvariantCulture, out var vnpAmountMinor))
            {
                _logger.LogWarning("Invalid VNPAY IPN amount for OrderCode {OrderCode}.", orderCode);
                return new VnPayIpnResponse(VnPayInvalidAmountCode, "Invalid amount");
            }

            var expectedAmountMinor = decimal.ToInt64(decimal.Round(transaction.AmountVnd * 100, 0, MidpointRounding.AwayFromZero));
            if (vnpAmountMinor != expectedAmountMinor)
            {
                _logger.LogWarning("VNPAY IPN amount mismatch for OrderCode {OrderCode}.", orderCode);
                return new VnPayIpnResponse(VnPayInvalidAmountCode, "Invalid amount");
            }

            if (transaction.Status != TransactionStatus.Pending)
            {
                _logger.LogInformation("VNPAY IPN transaction {OrderCode} already confirmed with status {Status}.", orderCode, transaction.Status);
                return new VnPayIpnResponse(VnPayOrderAlreadyConfirmedCode, "Order already confirmed");
            }

            var responseCode = GetQueryValue(queryParameters, "vnp_ResponseCode");
            var transactionStatus = GetQueryValue(queryParameters, "vnp_TransactionStatus");
            var isSuccess = string.Equals(responseCode, VnPaySuccessCode, StringComparison.Ordinal)
                && string.Equals(transactionStatus, VnPaySuccessCode, StringComparison.Ordinal);

            if (isSuccess)
            {
                transaction.Status = TransactionStatus.Success;
                transaction.PayOsTransactionId = GetQueryValue(queryParameters, "vnp_TransactionNo");
                transaction.CompletedAt = DateTime.UtcNow;

                var quota = await _quotaRepository.GetByWorkspaceIdAsync(transaction.WorkspaceId, cancellationToken);
                if (quota != null)
                {
                    quota.TotalAllocatedMinutes += transaction.PurchasedMinutes;
                    await _quotaRepository.UpdateAsync(quota, cancellationToken);

                    var auditLog = new QuotaAuditLog
                    {
                        Id = Guid.NewGuid(),
                        WorkspaceId = transaction.WorkspaceId,
                        Action = AuditAction.TopUp,
                        Amount = transaction.PurchasedMinutes,
                        BalanceAfter = quota.TotalAllocatedMinutes - quota.ConsumedMinutes,
                        ReferenceId = $"VNPAY_{orderCode}",
                        Description = $"VNPAY top up {transaction.PurchasedMinutes} mins from order {orderCode}",
                        CreatedAt = DateTime.UtcNow
                    };
                    await _auditLogRepository.AddAsync(auditLog, cancellationToken);
                }
                else
                {
                    _logger.LogWarning("Workspace {WorkspaceId} not found while processing VNPAY top up for OrderCode {OrderCode}.", transaction.WorkspaceId, orderCode);
                }
            }
            else
            {
                transaction.Status = TransactionStatus.Failed;
                transaction.CompletedAt = DateTime.UtcNow;
            }

            await _transactionRepository.UpdateAsync(transaction, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Processed verified VNPAY IPN for OrderCode {OrderCode} with status {Status}.", orderCode, transaction.Status);
            return new VnPayIpnResponse(VnPaySuccessCode, "Confirm Success");
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to persist VNPAY IPN result.");
            return new VnPayIpnResponse(VnPayUnknownErrorCode, "Unknown error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process VNPAY IPN.");
            return new VnPayIpnResponse(VnPayUnknownErrorCode, "Unknown error");
        }
    }

    public async Task<IEnumerable<Transaction>> GetTransactionsByWorkspaceAsync(Guid workspaceId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        return await _transactionRepository.GetByWorkspaceIdAsync(workspaceId, page, pageSize, cancellationToken);
    }

    public async Task<IEnumerable<QuotaAuditLog>> GetUsageLogsByWorkspaceAsync(Guid workspaceId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        return await _auditLogRepository.GetByWorkspaceIdAsync(workspaceId, page, pageSize, cancellationToken);
    }

    private bool VerifySignature(PayOsWebhookPayload payload)
    {
        var checksumKey = _configuration["PayOS:ChecksumKey"];
        var isDevelopment = string.Equals(
            _configuration["ASPNETCORE_ENVIRONMENT"],
            "Development",
            StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(checksumKey))
        {
            var allowInsecureDev = bool.TryParse(
                _configuration["Security:AllowInsecureWebhookSignatureInDevelopment"],
                out var parsedValue) && parsedValue;

            return isDevelopment && allowInsecureDev;
        }

        var data = payload.Data;
        if (data == null || string.IsNullOrWhiteSpace(payload.Signature))
        {
            return false;
        }

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = data.Amount.ToString(CultureInfo.InvariantCulture),
            ["code"] = payload.Code,
            ["currency"] = data.Currency,
            ["desc"] = payload.Desc,
            ["orderCode"] = data.OrderCode.ToString(CultureInfo.InvariantCulture),
            ["paymentLinkId"] = data.PaymentLinkId,
            ["reference"] = data.Reference,
            ["transactionDateTime"] = data.TransactionDateTime
        };

        var payloadToSign = string.Join("&", fields.Select(kvp => $"{kvp.Key}={kvp.Value}"));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(checksumKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadToSign));
        var computedSignature = Convert.ToHexString(hash).ToLowerInvariant();

        var providedSignature = payload.Signature.Trim().ToLowerInvariant();
        var computedBytes = Encoding.UTF8.GetBytes(computedSignature);
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature);

        return CryptographicOperations.FixedTimeEquals(computedBytes, providedBytes);
    }

    private bool VerifyVnPaySignature(IReadOnlyDictionary<string, string> queryParameters)
    {
        var hashSecret = _configuration["VNPay:HashSecret"] ?? _configuration["VnPay:HashSecret"];
        if (string.IsNullOrWhiteSpace(hashSecret))
        {
            return false;
        }

        var providedSignature = GetQueryValue(queryParameters, "vnp_SecureHash");
        if (string.IsNullOrWhiteSpace(providedSignature))
        {
            return false;
        }

        var hashData = BuildVnPayHashData(queryParameters);
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(hashSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(hashData));
        var computedSignature = Convert.ToHexString(hash).ToLowerInvariant();

        var computedBytes = Encoding.UTF8.GetBytes(computedSignature);
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature.Trim().ToLowerInvariant());
        return computedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(computedBytes, providedBytes);
    }

    private static string BuildVnPayHashData(IReadOnlyDictionary<string, string> queryParameters)
    {
        return string.Join(
            "&",
            queryParameters
                .Where(kvp => kvp.Key.StartsWith("vnp_", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(kvp.Key, "vnp_SecureHash", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(kvp.Key, "vnp_SecureHashType", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(kvp.Value))
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));
    }

    private static string GetQueryValue(IReadOnlyDictionary<string, string> queryParameters, string key)
    {
        return queryParameters.TryGetValue(key, out var value) ? value : string.Empty;
    }
}
