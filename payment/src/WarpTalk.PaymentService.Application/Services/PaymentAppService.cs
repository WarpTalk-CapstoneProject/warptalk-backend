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

    public async Task ProcessCheckoutSessionCompletedAsync(string stripeSessionId, string paymentIntentId, decimal amount, string currency, string userId, string paymentType)
    {
        // Call Billing API via gRPC directly without saving locally
        // Idempotency check is handled on the Billing side.
        var request = new ProcessPaymentRequest
        {
            UserId = userId,
            Amount = (double)amount,
            Currency = currency,
            PaymentType = paymentType,
            StripeSessionId = stripeSessionId
        };
        
        await _billingClient.ProcessPaymentSuccessAsync(request);
    }
}
