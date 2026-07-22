using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class InvoiceMapper
{
    public static InvoiceDto ToDto(this Invoice i, Guid workspaceId, string? workspaceName = null)
    {
        return new InvoiceDto
        {
            Id = i.Id.ToString(),
            InvoiceNumber = i.InvoiceNumber,
            Subtotal = i.Subtotal,
            Tax = i.Tax,
            Total = i.Total,
            Currency = i.Currency,
            Status = i.Status,
            PdfUrl = i.PdfUrl,
            LineItems = i.LineItems,
            IssuedAt = i.IssuedAt,
            DueAt = i.DueAt,
            PaidAt = i.PaidAt,
            CreatedAt = i.CreatedAt,
            WorkspaceId = workspaceId.ToString(),
            WorkspaceName = workspaceName
        };
    }
}
