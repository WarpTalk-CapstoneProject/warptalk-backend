using System;

namespace WarpTalk.BillingService.Application.DTOs;

public class InvoiceDto
{
    public string Id { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? PdfUrl { get; set; }
    public string LineItems { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? WorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
}
