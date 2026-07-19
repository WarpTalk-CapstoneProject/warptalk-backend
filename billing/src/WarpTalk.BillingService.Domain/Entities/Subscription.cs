using System;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Subscription
{
    public Guid Id { get; set; }

    /// <summary>External AuthService user id (creator/owner). No physical FK.</summary>
    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PlanId { get; set; }

    public string Status { get; set; } = null!;

    public int CreditsRemaining { get; set; }

    public int CreditsUsedThisCycle { get; set; }

    public DateTime CurrentPeriodStart { get; set; }

    public DateTime CurrentPeriodEnd { get; set; }

    public bool AutoRenew { get; set; } = true;

    public string? CancellationReason { get; set; }

    public DateTime? CancelledAt { get; set; }

    public DateTime? TrialEndsAt { get; set; }

    /// <summary>
    /// The authoritative "is this the workspace's active subscription" flag — driven off
    /// this boolean, not Status, because billing_worker's resolve_subscription() and the
    /// one-active-per-workspace unique index (migration 016/017) both key off is_active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public virtual Plan Plan { get; set; } = null!;

    public virtual ICollection<CreditTransaction> CreditTransactions { get; set; } = new List<CreditTransaction>();

    public virtual ICollection<CreditBalanceSnapshot> CreditBalanceSnapshots { get; set; } = new List<CreditBalanceSnapshot>();

    public virtual ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    // Concurrency Token (mapped to PostgreSQL xmin)
    public uint Version { get; set; }
}
