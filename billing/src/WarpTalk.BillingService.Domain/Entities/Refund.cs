using WarpTalk.BillingService.Domain.Constants;
using System;


namespace WarpTalk.BillingService.Domain.Entities;

public partial class Refund
{
    public Guid Id { get; set; }

    public Guid PaymentId { get; set; }

    public Guid UserId { get; set; }

    public decimal Amount { get; set; }

    public string? Reason { get; set; }

    public string Status { get; set; } = BillingConstants.RefundStatuses.Pending;

    public string? ProviderRefundId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public virtual Payment Payment { get; set; } = null!;
}
