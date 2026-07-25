using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.DTOs;

public sealed class PaymentEventContext
{
    public PaymentEventContext(
        StripePaymentEventRequest request,
        Guid workspaceId,
        Guid userId,
        string providerTransactionId,
        string parsedPaymentStatus,
        Guid paymentId,
        Payment? existingPayment,
        Subscription? subscription)
    {
        Request = request;
        WorkspaceId = workspaceId;
        UserId = userId;
        ProviderTransactionId = providerTransactionId;
        ParsedPaymentStatus = parsedPaymentStatus;
        PaymentId = paymentId;
        ExistingPayment = existingPayment;
        Subscription = subscription;
    }

    public StripePaymentEventRequest Request { get; }
    public Guid WorkspaceId { get; }
    public Guid UserId { get; }
    public string ProviderTransactionId { get; }
    public string ParsedPaymentStatus { get; }
    public Guid PaymentId { get; }
    public Payment? ExistingPayment { get; set; }
    public Subscription? Subscription { get; set; }
    public bool SubscriptionChanged { get; set; }
}
