using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// Mock PayOS implementation for local development testing
/// Simulates PayOS API responses without requiring network access
/// </summary>
public class MockPayOsService : IPayOsService
{
    private readonly ILogger<MockPayOsService> _logger;
    private static int _checkoutCounter = 0;

    public MockPayOsService(ILogger<MockPayOsService> logger)
    {
        _logger = logger;
    }

    public async Task<PayOsCheckoutResponse> CreateCheckoutLinkAsync(
        long orderCode,
        long amount,
        string description,
        string returnUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(100, cancellationToken); // Simulate API delay

        var checkoutId = $"MOCK_{++_checkoutCounter:D6}_{orderCode}";
        var checkoutUrl = $"https://mock-payos-checkout.local/checkout?id={checkoutId}";

        _logger.LogInformation(
            "[MOCK] Created PayOS checkout link for OrderCode {OrderCode}, Amount {Amount} VND",
            orderCode, amount);

        return new PayOsCheckoutResponse
        {
            Code = "00",
            Desc = "Success",
            Data = new PayOsCheckoutData
            {
                CheckoutUrl = checkoutUrl,
                QrCode = "MOCK_QR_CODE_" + checkoutId
            }
        };
    }

    public async Task<PayOsOrderDetailsResponse> GetCheckoutLinkDetailsAsync(
        long orderCode,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(50, cancellationToken);

        _logger.LogInformation("[MOCK] Retrieved PayOS transaction details for OrderCode {OrderCode}", orderCode);

        return new PayOsOrderDetailsResponse
        {
            Code = "00",
            Desc = "Success",
            Data = new PayOsOrderData
            {
                OrderCode = orderCode,
                Amount = 199000,
                AmountPaid = 199000,
                AmountRemaining = 0,
                Status = "COMPLETED",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                ExpiredAt = DateTime.UtcNow.AddHours(1),
                CanceledAt = "",
                Description = "Mock Payment",
                CheckoutUrl = $"https://mock-payos-checkout.local/checkout?id=MOCK_{orderCode}",
                ReturnUrl = "http://localhost:3000/checkout/result",
                CancelUrl = "http://localhost:3000/checkout/cancel",
                Reference = $"MOCK_REF_{orderCode}",
                Transactions = new[]
                {
                    new PayOsTransactionData
                    {
                        Reference = $"MOCK_TXN_{orderCode}",
                        Amount = 199000,
                        Status = "COMPLETED",
                        CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                        AccountNumber = "**** **** **** 1111",
                        AccountName = "Mock User",
                        Method = "CARD"
                    }
                }
            }
        };
    }

    public async Task<PayOsCancelResponse> CancelCheckoutLinkAsync(
        long orderCode,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(50, cancellationToken);

        _logger.LogInformation("[MOCK] Cancelled PayOS checkout link for OrderCode {OrderCode}", orderCode);

        return new PayOsCancelResponse
        {
            Code = "00",
            Desc = "Success",
            Data = new object()
        };
    }

    public async Task<PayOsRefundResponse> RefundAsync(
        string orderCode,
        long amount,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(100, cancellationToken);

        _logger.LogInformation(
            "[MOCK] Refunded {Amount} VND for OrderCode {OrderCode}. Reason: {Reason}",
            amount, orderCode, reason);

        return new PayOsRefundResponse
        {
            Code = "00",
            Desc = "Success",
            Data = new PayOsRefundData
            {
                RefundId = $"MOCK_REFUND_{orderCode}",
                OrderCode = orderCode,
                RefundAmount = amount,
                Status = "SUCCESS",
                CreatedAt = DateTime.UtcNow
            }
        };
    }
}

