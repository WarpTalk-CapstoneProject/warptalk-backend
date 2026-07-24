using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class RefundMapper
{
    public static RefundDto ToDto(this Refund refund)
    {
        return new RefundDto(
            Id: refund.Id.ToString(),
            PaymentId: refund.PaymentId.ToString(),
            Amount: refund.Amount,
            Reason: refund.Reason ?? string.Empty,
            Status: refund.Status.ToLower(),
            CreatedAt: refund.CreatedAt,
            CompletedAt: refund.CompletedAt
        );
    }

    public static Refund CreateRefund(Guid paymentId, decimal amount, string? reason, string status)
    {
        var now = DateTime.UtcNow;
        return new Refund
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            Amount = amount,
            Reason = reason,
            Status = status,
            CreatedAt = now,
            CompletedAt = status == Domain.Constants.PaymentConstants.PaymentStatuses.Refunded ? now : null
        };
    }
}
