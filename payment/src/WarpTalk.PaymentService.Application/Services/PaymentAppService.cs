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

    public async Task<CheckoutSessionDto> GetCheckoutSessionAsync(string sessionId)
    {
        if (sessionId.StartsWith("mock_session_"))
        {
            var payloadBase64 = sessionId.Substring("mock_session_".Length);
            var payloadJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
            var payload = System.Text.Json.JsonSerializer.Deserialize<MockSessionPayload>(payloadJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload == null)
            {
                throw new ArgumentException("Invalid mock session payload.");
            }

            var metadata = new Dictionary<string, string>
            {
                { "UserId", payload.UserId.ToString() },
                { "WorkspaceId", payload.WorkspaceId.ToString() },
                { "PaymentType", payload.PaymentType },
                { "PlanSlug", payload.PlanSlug ?? "" },
                { "BillingCycle", payload.BillingCycle ?? "" }
            };

            return new CheckoutSessionDto(
                sessionId,
                (long)(string.Equals(payload.Currency, "vnd", StringComparison.OrdinalIgnoreCase) ? payload.Amount : payload.Amount * 100),
                payload.Currency,
                metadata,
                "paid",
                "complete",
                "mock_pi_" + Guid.NewGuid().ToString("N")
            );
        }
        else
        {
            var service = new Stripe.Checkout.SessionService();
            var session = await service.GetAsync(sessionId);
            if (session == null)
            {
                throw new KeyNotFoundException("Session not found.");
            }

            var metadata = session.Metadata != null 
                ? session.Metadata.ToDictionary(k => k.Key, v => v.Value) 
                : new Dictionary<string, string>();

            return new CheckoutSessionDto(
                session.Id,
                session.AmountTotal,
                session.Currency,
                metadata,
                session.PaymentStatus,
                session.Status,
                session.PaymentIntentId
            );
        }
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

    private class MockSessionPayload
    {
        public Guid UserId { get; set; }
        public Guid WorkspaceId { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string PaymentType { get; set; } = string.Empty;
        public string? PlanSlug { get; set; }
        public string? BillingCycle { get; set; }
    }
}
