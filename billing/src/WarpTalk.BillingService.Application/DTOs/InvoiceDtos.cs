using System;

namespace WarpTalk.BillingService.Application.DTOs;

public record InvoiceDto(
    string Id,
    string InvoiceNumber,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    string Currency,
    string Status,
    string? PdfUrl,
    string LineItems,
    DateTime IssuedAt,
    DateTime? DueAt,
    DateTime? PaidAt,
    DateTime CreatedAt,
    string? WorkspaceId,
    string? WorkspaceName
);
