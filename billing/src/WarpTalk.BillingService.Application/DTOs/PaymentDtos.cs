using System;

namespace WarpTalk.BillingService.Application.DTOs;


public record PaymentTransactionDto(
    Guid     Id,
    Guid     SubscriptionId,
    decimal  Amount,
    decimal  TaxAmount,
    decimal  TotalAmount,
    string   Currency,
    string   PaymentMethod,
    string   Provider,
    string?  ProviderTransactionId,
    string?  ProviderOrderId,
    string   Status,            // pending | paid | failed | refunded
    string?  FailureReason,
    DateTime? PaidAt,
    DateTime CreatedAt
);
