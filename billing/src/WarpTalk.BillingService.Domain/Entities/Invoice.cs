using System;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Invoice
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid? SubscriptionId { get; set; }

    public Guid? PaymentId { get; set; }

    public string StripeInvoiceId { get; set; } = null!;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? InvoicePdfUrl { get; set; }

    public string? HostedInvoiceUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Subscription? Subscription { get; set; }
    
    public virtual Payment? Payment { get; set; }
}
