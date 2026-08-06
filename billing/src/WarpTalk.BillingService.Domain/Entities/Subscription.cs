using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;


namespace WarpTalk.BillingService.Domain.Entities;

public partial class Subscription
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PlanId { get; set; }

    public string Status { get; set; } = SubscriptionConstants.SubscriptionStatuses.Active;

    public int CreditsRemaining { get; set; }

    [NotMapped]
    public int CurrentCredits
    {
        get => CreditsRemaining;
        set => CreditsRemaining = value;
    }

    public int CreditsUsedThisCycle { get; set; }

    public DateTime CurrentPeriodStart { get; set; }

    public DateTime CurrentPeriodEnd { get; set; }

    [NotMapped]
    public DateTime StartDate
    {
        get => CurrentPeriodStart;
        set => CurrentPeriodStart = value;
    }

    [NotMapped]
    public DateTime? EndDate
    {
        get => CurrentPeriodEnd;
        set => CurrentPeriodEnd = value ?? default;
    }

    public bool AutoRenew { get; set; } = true;

    public string? CancellationReason { get; set; }

    public DateTime? CancelledAt { get; set; }

    public DateTime? TrialEndsAt { get; set; }

    public int? CreditsPerCycleOverride { get; set; }

    public decimal? ContractPriceVnd { get; set; }

    public int? OverageCapCreditsOverride { get; set; }

    public decimal? OveragePricePerCreditOverride { get; set; }

    public int? InvoiceTermsDaysOverride { get; set; }

    /// <summary>
    /// WT-263: contract-negotiated entitlement overrides, as a jsonb object keyed by entitlement key
    /// (<c>{"max_languages": 5, "voice_clone": true}</c>). Layer 3 of the resolution order.
    ///
    /// The typed <c>*_override</c> columns above it carry COMMERCIAL terms — credits, overage cap,
    /// invoice days. None of them is a capability, which is why the entitlement layer needed its own
    /// storage rather than reusing one of them. Unlike the workspace layer, a contract override may
    /// loosen as well as tighten: the contract is the agreement, so it outranks the catalog row in
    /// both directions.
    /// </summary>
    public string? EntitlementOverrides { get; set; }

    public string? BillingContactEmail { get; set; }

    public int OverageCreditsThisCycle { get; set; }

    public DateTime? OverageStartedAt { get; set; }

    public string ServiceState { get; set; } = SubscriptionConstants.ServiceStates.Healthy;

    public string? SuspendedReason { get; set; }

    public string? OwnerEmailDomain { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public virtual Plan Plan { get; set; } = null!;

    public virtual ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    public virtual ICollection<CreditTransaction> CreditTransactions { get; set; } = new List<CreditTransaction>();

    public virtual ICollection<CreditBalanceSnapshot> CreditBalanceSnapshots { get; set; } = new List<CreditBalanceSnapshot>();

    public virtual ICollection<UsageRecord> UsageRecords { get; set; } = new List<UsageRecord>();

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    public uint Version { get; set; }

}
