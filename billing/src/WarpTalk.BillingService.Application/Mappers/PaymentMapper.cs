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
        payment.Status,
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
            Status = "pending",
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
