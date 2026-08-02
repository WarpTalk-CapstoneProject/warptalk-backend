using WarpTalk.BillingService.Domain.Constants;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Domain.Entities;


namespace WarpTalk.BillingService.Application.Mappers;

public static class PaymentMapper
{
    public static PaymentTransactionDto ToDto(this Payment payment) => new(
        payment.Id,
        payment.SubscriptionId,
        payment.Amount,
        payment.TaxAmount,
        payment.TotalAmount,
        payment.Currency,
        payment.PaymentMethod,
        payment.Provider,
        payment.ProviderTransactionId,
        payment.ProviderOrderId,
        payment.Status.ToLower(),
        payment.FailureReason,
        payment.PaidAt,
        payment.CreatedAt
    );

    public static Payment ToEntity(this CreatePaymentRequest request, decimal amount, string currency, decimal taxAmount = 0)
    {
        var now = DateTime.UtcNow;
        return new Payment
        {
            Id = Guid.NewGuid(),
            SubscriptionId = request.SubscriptionId,
            UserId = request.UserId,
            Amount = amount,
            TaxAmount = taxAmount,
            TotalAmount = amount + taxAmount,
            Currency = currency,
            PaymentMethod = request.PaymentMethod,
            Provider = request.Provider,
            Status = PaymentConstants.PaymentStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Payment CreateStripePayment(StripePaymentCreationRequest request)
    {
        var now = DateTime.UtcNow;
        var paidAt = string.Equals(request.Status, PaymentConstants.PaymentStatuses.Paid, StringComparison.OrdinalIgnoreCase)
            ? now
            : (DateTime?)null;

        return new Payment
        {
            Id = Guid.NewGuid(),
            SubscriptionId = request.SubscriptionId ?? Guid.Empty,
            UserId = request.UserId,
            Amount = request.Amount,
            TaxAmount = 0m,
            TotalAmount = request.Amount,
            Currency = request.Currency ?? PaymentConstants.Currencies.Usd,
            PaymentMethod = PaymentConstants.PaymentMethods.Card,
            Provider = PaymentConstants.Providers.Stripe,
            ProviderTransactionId = request.ProviderTransactionId,
            Status = request.Status,
            FailureReason = request.FailureReason,
            PaidAt = paidAt,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Payment CreateBillingCyclePayment(BillingCyclePaymentCreationRequest request)
    {
        if (request.Subscription.Plan is null)
            throw new ArgumentException("Billing cycle payment requires subscription.Plan to be loaded.", nameof(request));

        return new Payment
        {
            Id = Guid.NewGuid(),
            SubscriptionId = request.Subscription.Id,
            UserId = request.Subscription.UserId,
            Amount = request.Subtotal,
            TaxAmount = request.Tax,
            TotalAmount = request.Total,
            Currency = request.Subscription.Plan.Currency,
            PaymentMethod = PaymentConstants.PaymentMethods.Invoice,
            Provider = PaymentConstants.Providers.InternalInvoice,
            ProviderTransactionId = BillingCycleTransactionIdHelper.Create(request.Subscription.Id, request.Subscription.CurrentPeriodEnd),
            Status = PaymentConstants.PaymentStatuses.Pending,
            ProviderMetadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason = InvoiceConstants.BillingReasons.SubscriptionCycle,
                periodStart = request.Subscription.CurrentPeriodStart,
                periodEnd = request.Subscription.CurrentPeriodEnd,
                overageCredits = request.OverageCredits
            }),
            CreatedAt = request.Now,
            UpdatedAt = request.Now
        };
    }

}
