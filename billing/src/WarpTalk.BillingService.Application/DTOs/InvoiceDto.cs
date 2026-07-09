namespace WarpTalk.BillingService.Application.DTOs;

public class InvoiceDto
{
    public string Id { get; set; } = string.Empty;
    public string StripeInvoiceId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? InvoicePdfUrl { get; set; }
    public string? HostedInvoiceUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? WorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
}
