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

    /// <summary>
    /// G11: set by a handler whose change moves an entitlement (an add-on bought, renewed or
    /// cancelled). The caller re-resolves and publishes AFTER committing, because the resolver
    /// reads the committed rows — an add-on only added to the unit of work is invisible to it.
    /// </summary>
    public bool EntitlementsChanged { get; set; }
}
