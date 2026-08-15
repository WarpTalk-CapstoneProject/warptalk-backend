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
            // THE WT-370 KILLER. Found in production postgres logs:
            //
            //   ERROR: null value in column "due_at" of relation "invoices"
            //          violates not-null constraint
            //
            // Migration 045 made due_at NOT NULL and backfilled the existing rows; this mapper
            // was never updated to set it. Every Stripe payment therefore threw on
            // SaveChangesAsync — and because ProcessPaymentEventAsync writes the payment, the
            // invoice and the subscription in ONE transaction, the throw rolled all three back.
            // The money was taken, the workspace got no plan, and the webhook answered 200.
            //
            // `now`, not now + terms: this invoice is constructed already Paid, with PaidAt set
            // on the line above. An invoice that is settled at the moment it is issued was due
            // then. Net terms belong to the contract-invoice path, which has its own mapper and
            // its own InvoiceTermsDays.
            DueAt = now,
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
