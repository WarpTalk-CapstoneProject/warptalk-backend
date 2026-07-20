using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>Maps to subscription.payments — kept the class name "Transaction" to avoid renaming every caller.</summary>
public partial class Transaction
{
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    /// <summary>External AuthService user id who made the payment. No physical FK.</summary>
    public Guid UserId { get; set; }

    public decimal Amount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = "USD";

    public string PaymentMethod { get; set; } = "unknown";

    public string Provider { get; set; } = "stripe";

    public string? ProviderTransactionId { get; set; }

    public string? ProviderOrderId { get; set; }

    public TransactionStatus Status { get; set; }

    public string? FailureReason { get; set; }

    public string? ProviderMetadata { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? RefundedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>ExternalId kept for BillingService.cs's existing MapToDto — mirrors ProviderTransactionId.</summary>
    public string? ExternalId
    {
        get => ProviderTransactionId;
        set => ProviderTransactionId = value;
    }

    public virtual Subscription? Subscription { get; set; }
}
