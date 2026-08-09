using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Models;

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

    public static Invoice CreateStripeInvoice(StripeInvoiceCreationRequest request)
    {
        var now = DateTime.UtcNow;
        string invoiceNum = InvoiceConstants.Formats.InvoiceNumberPrefix + now.ToString("yyyyMMdd") + "-" + request.PaymentId.ToString().Substring(0, 8).ToUpper();
        return new Invoice
        {
            Id = Guid.NewGuid(),
            PaymentId = request.PaymentId,
            UserId = request.UserId,
            InvoiceNumber = invoiceNum,
            Subtotal = request.Amount,
            Tax = 0,
            Total = request.Amount,
            Currency = request.Currency ?? PaymentConstants.Currencies.Usd,
            Status = InvoiceConstants.InvoiceStatuses.Paid,
            PdfUrl = request.PdfUrl,
            LineItems = InvoiceConstants.Defaults.EmptyLineItems,
            IssuedAt = now,
            DueAt = now,   // Stripe invoices are paid immediately; due date = issued date
            PaidAt = now,
            CreatedAt = now
        };
    }

    public static Invoice CreateBillingCycleInvoice(BillingCycleInvoiceCreationRequest request)
    {
        if (request.Subscription.Plan is null)
            throw new ArgumentException("Billing cycle invoice requires subscription.Plan to be loaded.", nameof(request));

        return new Invoice
        {
            Id = Guid.NewGuid(),
            PaymentId = request.PaymentId,
            UserId = request.Subscription.UserId,
            InvoiceNumber = $"{InvoiceConstants.Formats.InvoiceNumberPrefix}{request.Now:yyyyMMdd}-{request.PaymentId.ToString("N")[..8].ToUpperInvariant()}",
            Subtotal = request.Subtotal,
            Tax = request.Tax,
            Total = request.Total,
            Currency = request.Plan.Currency,
            Status = InvoiceConstants.InvoiceStatuses.Open,
            LineItems = CreateBillingCycleLineItems(request),
            IssuedAt = request.Now,
            DueAt = request.Now.AddDays(request.InvoiceTermsDays),
            CreatedAt = request.Now
        };
    }

    private static string CreateBillingCycleLineItems(BillingCycleInvoiceCreationRequest request)
    {
        var lineItems = new List<object>
        {
            new
            {
                type = InvoiceConstants.LineItemTypes.Subscription,
                description = request.Plan.Name,
                quantity = 1,
                unitPrice = (decimal?)null,
                amount = request.ContractPrice
            },
            new
            {
                type = InvoiceConstants.LineItemTypes.Overage,
                description = InvoiceConstants.LineItemDescriptions.UsageOverCommittedCredits,
                quantity = request.OverageCredits,
                unitPrice = request.OveragePricePerCredit,
                amount = request.OverageAmount
            }
        };

        foreach (var item in request.UsageBreakdown)
        {
            lineItems.Add(new
            {
                type = InvoiceConstants.LineItemTypes.UsageBreakdown,
                chargeType = item.ChargeType,
                unit = item.Unit,
                quantity = item.Quantity,
                creditsConsumed = item.CreditsConsumed
            });
        }

        return System.Text.Json.JsonSerializer.Serialize(lineItems);
    }
}
