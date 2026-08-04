using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Plan
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public string Tier { get; set; } = null!;

    public decimal Price { get; set; }

    public string Currency { get; set; } = PaymentConstants.Currencies.VndAccounting;

    public string BillingCycle { get; set; } = SubscriptionConstants.BillingCycles.Monthly;

    public int CreditsPerCycle { get; set; }

    public int OverageCapCredits { get; set; }

    public decimal OveragePricePerCredit { get; set; } = SubscriptionConstants.PlanDefaults.OveragePricePerCredit;

    public int LowBalanceThresholdCredits { get; set; }

    public int RolloverCapCredits { get; set; }

    public int InvoiceTermsDays { get; set; } = SubscriptionConstants.PlanDefaults.InvoiceTermsDays;

    public int InvoiceGraceHours { get; set; } = SubscriptionConstants.PlanDefaults.InvoiceGraceHours;

    public int MaxParticipants { get; set; } = SubscriptionConstants.PlanDefaults.MaxParticipants;

    public int MaxLanguages { get; set; } = SubscriptionConstants.PlanDefaults.MaxLanguages;

    public bool VoiceCloneEnabled { get; set; }

    public bool AiAssistantEnabled { get; set; }

    public bool GlossaryEnabled { get; set; }

    public bool DedicatedGpu { get; set; }

    public string Features { get; set; } = SubscriptionConstants.FeatureAccess.EmptyFeaturesJson;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public virtual ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
}
