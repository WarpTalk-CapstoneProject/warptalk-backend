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

    public async Task<string> CreateCheckoutSessionAsync(WarpTalk.PaymentService.Application.DTOs.CreateCheckoutSessionRequest request)
    {
        if (request.WorkspaceId == Guid.Empty)
            throw new ArgumentException("WorkspaceId is required.", nameof(request.WorkspaceId));

        return await _stripePaymentService.CreateCheckoutSessionAsync(
            request.UserId,
            request.WorkspaceId,
            request.Amount,
            request.Currency,
            request.PaymentType,
            request.PlanSlug,
            request.BillingCycle
        );
    }

    public async Task ProcessPaymentEventAsync(string stripeSessionId, string paymentIntentId, decimal amount, string currency, string userId, string workspaceId, string paymentType, string status, string failureReason = "", string invoiceUrl = "", string invoicePdf = "", string planSlug = "", string billingCycle = "")
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
            FailureReason = failureReason ?? string.Empty,
            WorkspaceId = workspaceId ?? string.Empty,
            InvoiceUrl = invoiceUrl ?? string.Empty,
            InvoicePdf = invoicePdf ?? string.Empty,
            PlanSlug = planSlug ?? string.Empty,
            BillingCycle = billingCycle ?? string.Empty
        };

        await _billingClient.ProcessPaymentEventAsync(request);
    }
}
