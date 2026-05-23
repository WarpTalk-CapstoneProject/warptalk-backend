using System;

namespace WarpTalk.PaymentService.Application.DTOs;

public record CreateCheckoutSessionRequest
{
    public Guid UserId { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "usd";
    public string PaymentType { get; init; } = string.Empty; // e.g. "CreditTopUp", "Subscription"
}
