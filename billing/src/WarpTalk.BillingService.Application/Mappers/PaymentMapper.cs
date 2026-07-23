using WarpTalk.BillingService.Domain.Constants;
using System;
using WarpTalk.BillingService.Application.DTOs;
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
            Status = BillingConstants.PaymentStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Payment ToEntity(this TopUpRequest request, Subscription sub, Guid paymentId, decimal amount, string currency) => new()
    {
        Id = paymentId,
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        Amount = amount,
        TaxAmount = 0m,
        TotalAmount = amount,
        Currency = currency,
        PaymentMethod = Domain.Constants.BillingConstants.Providers.TopUpSimulation,
        Provider = Domain.Constants.BillingConstants.Providers.Stripe,
        ProviderTransactionId = request.ReferenceId?.ToString() ?? Guid.NewGuid().ToString(),
        Status = BillingConstants.PaymentStatuses.Paid,
        PaidAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public static Payment ToSimulatedEntity(this Subscription sub, Guid paymentId, string stripeInvoiceId, decimal amount, string currency) => new()
    {
        Id = paymentId,
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        Amount = amount,
        TaxAmount = 0m,
        TotalAmount = amount,
        Currency = currency.ToLowerInvariant(),
        PaymentMethod = Domain.Constants.BillingConstants.Providers.StripeSimulation,
        Provider = Domain.Constants.BillingConstants.Providers.Stripe,
        ProviderTransactionId = stripeInvoiceId,
        Status = BillingConstants.PaymentStatuses.Paid,
        PaidAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}
