using WarpTalk.BillingService.Domain.Constants;
using System;


namespace WarpTalk.BillingService.Domain.Entities;

public partial class Transaction
{
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    public Guid UserId { get; set; }

    public decimal Amount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = "USD";

    public string PaymentMethod { get; set; } = "unknown";

    public string Provider { get; set; } = "stripe";

    public string? ProviderTransactionId { get; set; }

    public string? ProviderOrderId { get; set; }

    public string Status { get; set; } = TransactionConstants.TransactionStatuses.Pending;

    public string? FailureReason { get; set; }

    public string? ProviderMetadata { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? RefundedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? ExternalId
    {
        get => ProviderTransactionId;
        set => ProviderTransactionId = value;
    }

    public virtual Subscription? Subscription { get; set; }
}
