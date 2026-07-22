using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class RefundMapper
{
    public static RefundDto ToDto(this Refund refund)
    {
        return new RefundDto
        {
            Id = refund.Id.ToString(),
            PaymentId = refund.PaymentId.ToString(),
            Amount = refund.Amount,
            Reason = refund.Reason ?? string.Empty,
            Status = refund.Status,
            CreatedAt = refund.CreatedAt,
            CompletedAt = refund.CompletedAt
        };
    }
}
