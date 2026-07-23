using WarpTalk.BillingService.Domain.Constants;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class InvoiceMapper
{
    public static InvoiceDto ToDto(this Invoice i, Guid workspaceId, string? workspaceName = null)
    {
        return new InvoiceDto(
            Id: i.Id.ToString(),
            InvoiceNumber: i.InvoiceNumber,
            Subtotal: i.Subtotal,
            Tax: i.Tax,
            Total: i.Total,
            Currency: i.Currency,
            Status: i.Status.ToLower(),
            PdfUrl: i.PdfUrl,
            LineItems: i.LineItems,
            IssuedAt: i.IssuedAt,
            DueAt: i.DueAt,
            PaidAt: i.PaidAt,
            CreatedAt: i.CreatedAt,
            WorkspaceId: workspaceId.ToString(),
            WorkspaceName: workspaceName
        );
     }

    public static Invoice ToEntity(this TopUpRequest request, Payment payment) => new()
    {
        Id = Guid.NewGuid(),
        UserId = payment.UserId,
        PaymentId = payment.Id,
        InvoiceNumber = payment.ProviderTransactionId,
        Subtotal = payment.Amount,
        Tax = payment.TaxAmount,
        Total = payment.TotalAmount,
        Currency = payment.Currency,
        Status = BillingConstants.InvoiceStatuses.Paid,
        PdfUrl = string.Empty,
        LineItems = System.Text.Json.JsonSerializer.Serialize(new[] {
            new {
                description = $"{request.Amount} cr Credit Top-Up Package",
                quantity = 1,
                amount = payment.Amount
            }
        }),
        IssuedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    // TODO: This method is used for internal simulation/testing only.
    // The ProviderTransactionId is a randomly generated mock ID (not a real Stripe invoice ID),
    // so PdfUrl is a non-functional placeholder URL.
    // When real Stripe Webhook integration is implemented (invoice.payment_succeeded event),
    // replace PdfUrl with the actual invoice_pdf URL returned by the Stripe API.
    public static Invoice ToSimulatedEntity(this Payment payment) => new()
    {
        Id = Guid.NewGuid(),
        UserId = payment.UserId,
        PaymentId = payment.Id,
        InvoiceNumber = payment.ProviderTransactionId,
        Subtotal = payment.Amount,
        Tax = payment.TaxAmount,
        Total = payment.TotalAmount,
        Currency = payment.Currency,
        Status = BillingConstants.InvoiceStatuses.Paid,
        // TODO: Placeholder only — this URL is not functional.
        // Replace with real Stripe invoice PDF URL from Stripe Webhook event payload.
        PdfUrl = $"https://stripe.com/invoice/{payment.ProviderTransactionId}",
        LineItems = System.Text.Json.JsonSerializer.Serialize(new[] {
            new {
                description = "WarpTalk Subscription Simulation Package",
                quantity = 1,
                amount = payment.Amount
            }
        }),
        IssuedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };
}
