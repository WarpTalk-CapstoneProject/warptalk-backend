using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Application.Services;

public class PaymentService : IPaymentService
{
    private readonly ITransactionService _transactionService;
    private readonly ISubscriptionPlanService _planService;
    private readonly IQuotaService _quotaService;
    private readonly IWorkspaceOwnershipResolver _workspaceResolver;
    private readonly IPayOsService _payOsService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        ITransactionService transactionService,
        ISubscriptionPlanService planService,
        IQuotaService quotaService,
        IWorkspaceOwnershipResolver workspaceResolver,
        IPayOsService payOsService,
        IConfiguration configuration,
        ILogger<PaymentService> logger)
    {
        _transactionService = transactionService;
        _planService = planService;
        _quotaService = quotaService;
        _workspaceResolver = workspaceResolver;
        _payOsService = payOsService;
        _configuration = configuration;
        _logger = logger;
    }

    // ======================================================
    // CREATE PAYMENT
    // ======================================================

    public async Task<PaymentLinkResponse> CreatePaymentLinkAsync(
        Guid workspaceId,
        CreatePaymentLinkRequest request,
        CancellationToken ct = default)
    {
        var ownerId = await _workspaceResolver.ResolveOwnerUserIdAsync(workspaceId, ct);
        return await CreatePaymentLinkByOwnerAsync(ownerId, request, ct);
    }

    public async Task<PaymentLinkResponse> CreatePaymentLinkByOwnerAsync(
        Guid ownerUserId,
        CreatePaymentLinkRequest request,
        CancellationToken ct = default)
    {
        decimal amount;
        decimal credits;
        string description;

        // ================= PLAN =================
        if (request.PlanId.HasValue)
        {
            var plan = await _planService.GetByIdAsync(request.PlanId.Value, ct);

            if (plan == null)
                throw new InvalidOperationException("Plan not found.");

            // FIX: no Price field → mock pricing
            credits = plan.IncludedCredits;
            amount = credits * 1000;

            description = $"Upgrade to {plan.GetType}";
        }
        // ================= TOPUP =================
        else if (request.TopUpMinutes.HasValue)
        {
            credits = request.TopUpMinutes.Value;
            amount = credits * 500;

            description = $"Top-up {credits} minutes";
        }
        else
        {
            throw new InvalidOperationException("Invalid request.");
        }

        var orderCode = GenerateOrderCode();

        await _transactionService.CreateAsync(new CreateTransactionCommand
        {
            WorkspaceId = ownerUserId,
            OwnerUserId = ownerUserId,
            PlanId = request.PlanId,
            OrderCode = orderCode,
            AmountVnd = amount,

            Type = request.PlanId.HasValue
        ? TransactionType.SubscriptionPurchase
        : TransactionType.CreditTopUp
        }, ct);

        var payOs = await _payOsService.CreateCheckoutLinkAsync(
            orderCode,
            (long)amount,
            description,
            _configuration["PayOS:ReturnUrl"]!,
            _configuration["PayOS:CancelUrl"]!,
            ct);

        _logger.LogInformation("Payment created Owner={Owner} Order={Order}", ownerUserId, orderCode);

        return new PaymentLinkResponse
        {
            CheckoutUrl = payOs.Data.CheckoutUrl,
            OrderCode = orderCode,
            Status = "Pending"
        };
    }

    // ======================================================
    // WEBHOOK
    // ======================================================

    public async Task<PayOsWebhookProcessResult> ProcessPayOsWebhookAsync(
        PayOsWebhookPayload payload,
        CancellationToken ct = default)
    {
        if (!VerifySignature(payload))
            throw new UnauthorizedAccessException("Invalid signature");

        var orderCode = payload.Data?.OrderCode ?? 0;

        if (orderCode == 0)
            return Fail(orderCode, "Invalid payload");

        var transaction = await _transactionService.GetByOrderCodeAsync(orderCode, ct);

        if (transaction == null)
            return Fail(orderCode, "Transaction not found");

        if (transaction.Status == TransactionStatus.Success)
            return Ok(orderCode, "Already processed");

        var success = payload.Code == "00";

        if (success)
        {
            await _transactionService.UpdateStatusAsync(
                transaction.Id,
                TransactionStatus.Success,
                ct);

            if (transaction.OwnerUserId == null)
                return Fail(orderCode, "Missing owner");

            var ownerId = transaction.OwnerUserId.Value;

            if (transaction.PlanId.HasValue)
            {
                await _quotaService.UpgradePlanByOwnerAsync(
                    ownerId,
                    transaction.PlanId.Value,
                    ct);
            }
            else
            {
                // FIX: no PurchasedCredits → derive from amount
                var credits = transaction.AmountVnd / 500;

                await _quotaService.TopUpQuotaByOwnerAsync(
                    ownerId,
                    credits,
                    $"PAYOS_{orderCode}",
                    ct);
            }
        }
        else
        {
            await _transactionService.UpdateStatusAsync(
                transaction.Id,
                TransactionStatus.Failed,
                ct);
        }

        return Ok(orderCode, success ? "Success" : "Failed");
    }

    // ======================================================
    // HELPERS
    // ======================================================

    private static long GenerateOrderCode()
    {
        var time = DateTime.UtcNow.ToString("yyMMddHHmmss");
        var rand = RandomNumberGenerator.GetInt32(100, 999);
        return long.Parse(time + rand, CultureInfo.InvariantCulture);
    }

    private bool VerifySignature(PayOsWebhookPayload payload)
    {
        var key = _configuration["PayOS:ChecksumKey"];
        if (string.IsNullOrWhiteSpace(key)) return false;

        if (payload.Data == null || string.IsNullOrWhiteSpace(payload.Signature))
            return false;

        var raw = $"{payload.Data.OrderCode}{payload.Data.Amount}{payload.Code}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(raw));

        var signature = Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature),
            Encoding.UTF8.GetBytes(payload.Signature.Trim().ToLowerInvariant()));
    }

    private static PayOsWebhookProcessResult Ok(long order, string msg) => new()
    {
        Success = true,
        OrderCode = order,
        Message = msg,
        TransactionStatus = "Success"
    };

    private static PayOsWebhookProcessResult Fail(long order, string msg) => new()
    {
        Success = false,
        OrderCode = order,
        Message = msg,
        TransactionStatus = "Failed"
    };
}