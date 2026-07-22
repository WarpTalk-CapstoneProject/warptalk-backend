using System;

namespace WarpTalk.BillingService.Application.DTOs;


public record PaymentTransactionDto(
    Guid Id,
    Guid SubscriptionId,
    decimal Amount,
    decimal TaxAmount,
    decimal TotalAmount,
    string Currency,
    string PaymentMethod,
    string Provider,
    string? ProviderTransactionId,
    string? ProviderOrderId,
    string Status,            // pending | paid | failed | refunded
    string? FailureReason,
    DateTime? PaidAt,
    DateTime CreatedAt
);

public record CreatePaymentRequest(
    Guid SubscriptionId,
    Guid UserId,
    string PaymentMethod,
    string Provider
);

public record PaymentWebhookRequest(
    string OrderCode, // Maps to Payment.Id
    string Status,    // "PAID", "CANCELLED", etc.
    string TransactionId
);

public class RefundDto
{
    public string Id { get; set; } = string.Empty;
    public string PaymentId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public record RefundPaymentRequest(
    Guid PaymentId,
    decimal Amount,
    string Reason
);

public record UpdatePaymentStatusRequest(
    string Status,
    string? ProviderTransactionId,
    string? FailureReason
);



