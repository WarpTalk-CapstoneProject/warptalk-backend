using System;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class CreditBalanceSnapshot
{
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    public int CreditsRemaining { get; set; }

    public int CreditsUsedThisCycle { get; set; }

    public DateTime SnapshotAt { get; set; }

    public virtual Subscription Subscription { get; set; } = null!;
}
