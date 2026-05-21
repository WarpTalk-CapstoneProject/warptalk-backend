using System;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Invoice
{
    public Guid Id { get; set; }

    public Guid PaymentId { get; set; }

    public Guid UserId { get; set; }

    public string InvoiceNumber { get; set; } = null!;

    public decimal Subtotal { get; set; }

    public decimal Tax { get; set; }

    public decimal Total { get; set; }

    public string Currency { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? PdfUrl { get; set; }

    public string LineItems { get; set; } = null!;

    public DateTime IssuedAt { get; set; }

    public DateTime? DueAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Payment Payment { get; set; } = null!;
}
