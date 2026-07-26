using System;
using System.Collections.Generic;

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

public record RefundDto(
    string Id,
    string PaymentId,
    decimal Amount,
    string Reason,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt
);

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

public record SendBillingNotificationsRequest(
    IEnumerable<Guid> UserIds,
    string Type,
    string Title,
    string Body,
    string ActionUrl,
    Dictionary<string, string>? Metadata = null
);

public record SendSingleNotificationRequest(
    Guid UserId,
    string Type,
    string Title,
    string Body,
    string ActionUrl,
    Dictionary<string, string>? Metadata = null
);

public record UpdateStripeSubscriptionRequest(
    Guid WorkspaceId,
    decimal NewAmount,
    string Currency,
    string PlanSlug
);

public record StripePaymentCreationRequest(
    Guid? SubscriptionId,
    Guid UserId,
    decimal Amount,
    string? Currency,
    string ProviderTransactionId,
    string Status,
    string? FailureReason
);

public record StripeInvoiceCreationRequest(
    Guid PaymentId,
    Guid UserId,
    decimal Amount,
    string? Currency,
    string? PdfUrl
);

public record TopUpPaymentCreationRequest(
    TopUpRequest TopUpRequest,
    Domain.Entities.Subscription Subscription,
    Guid PaymentId,
    decimal Amount,
    string Currency
);

public record SimulatedPaymentCreationRequest(
    Domain.Entities.Subscription Subscription,
    Guid PaymentId,
    string StripeInvoiceId,
    decimal Amount,
    string Currency
);

public record BillingCyclePaymentCreationRequest(
    Domain.Entities.Subscription Subscription,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    int OverageCredits,
    DateTime Now
);

public record BillingCycleInvoiceCreationRequest(
    Domain.Entities.Subscription Subscription,
    Domain.Entities.Plan Plan,
    Guid PaymentId,
    decimal ContractPrice,
    int OverageCredits,
    decimal OveragePricePerCredit,
    decimal OverageAmount,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    int InvoiceTermsDays,
    DateTime Now
);
