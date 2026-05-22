using System;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class CreditTransaction
{
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    public Guid UserId { get; set; }

    public int Amount { get; set; }

    public string Type { get; set; } = null!;

    public string? Description { get; set; }

    public Guid? ReferenceId { get; set; }

    public string? ReferenceType { get; set; }

    public string? CorrelationId { get; set; }

    public string Status { get; set; } = "committed";

    public int BalanceAfter { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Subscription Subscription { get; set; } = null!;
}
