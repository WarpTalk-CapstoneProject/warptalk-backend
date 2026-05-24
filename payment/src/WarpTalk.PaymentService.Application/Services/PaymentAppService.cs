using System;
using System.Threading.Tasks;
using WarpTalk.PaymentService.Application.DTOs;
using WarpTalk.PaymentService.Application.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.PaymentService.Application.Services;

public class PaymentAppService : IPaymentAppService
{
    private readonly IStripePaymentService _stripePaymentService;
    private readonly BillingService.BillingServiceClient _billingClient;

    public PaymentAppService(
        IStripePaymentService stripePaymentService,
        BillingService.BillingServiceClient billingClient)
    {
        _stripePaymentService = stripePaymentService;
        _billingClient = billingClient;
    }

    public async Task<string> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request)
    {
        return await _stripePaymentService.CreateCheckoutSessionAsync(
            request.UserId,
            request.Amount,
            request.Currency,
            request.PaymentType
        );
    }

    public async Task ProcessPaymentEventAsync(string stripeSessionId, string paymentIntentId, decimal amount, string currency, string userId, string paymentType, string status, string failureReason = "")
    {
        var request = new ProcessPaymentEventRequest
        {
            UserId = userId,
            Amount = (double)amount,
            Currency = currency ?? "usd",
            PaymentType = paymentType ?? "Unknown",
            StripeSessionId = stripeSessionId ?? string.Empty,
            ProviderTransactionId = paymentIntentId ?? string.Empty,
            Status = status,
            FailureReason = failureReason ?? string.Empty
        };

        await _billingClient.ProcessPaymentEventAsync(request);
    }
}
