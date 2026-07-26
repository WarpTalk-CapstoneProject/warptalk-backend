using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Collections.Generic;
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
        InvoiceNumber = payment.ProviderTransactionId ?? payment.Id.ToString("N"),
        Subtotal = payment.Amount,
        Tax = payment.TaxAmount,
        Total = payment.TotalAmount,
        Currency = payment.Currency,
        Status = InvoiceConstants.InvoiceStatuses.Paid,
        PdfUrl = string.Empty,
        LineItems = System.Text.Json.JsonSerializer.Serialize(new[] {
            new {
                description = string.Format(BillingMessageConstants.InvoiceMessages.TopUpPackageTemplate, request.Amount),
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
        InvoiceNumber = payment.ProviderTransactionId ?? payment.Id.ToString("N"),
        Subtotal = payment.Amount,
        Tax = payment.TaxAmount,
        Total = payment.TotalAmount,
        Currency = payment.Currency,
        Status = InvoiceConstants.InvoiceStatuses.Paid,
        // TODO: Placeholder only — this URL is not functional.
        // Replace with real Stripe invoice PDF URL from Stripe Webhook event payload.
        PdfUrl = string.Format(InvoiceConstants.Formats.StripeInvoiceUrlTemplate, payment.ProviderTransactionId),
        LineItems = System.Text.Json.JsonSerializer.Serialize(new[] {
            new {
                description = BillingMessageConstants.InvoiceMessages.SimulationPackage,
                quantity = 1,
                amount = payment.Amount
            }
        }),
        IssuedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    public static Invoice CreateStripeInvoice(StripeInvoiceCreationRequest request)
    {
        string invoiceNum = InvoiceConstants.Formats.InvoiceNumberPrefix + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + request.PaymentId.ToString().Substring(0, 8).ToUpper();
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
            IssuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
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
