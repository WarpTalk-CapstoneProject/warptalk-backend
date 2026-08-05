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

    /// <summary>
    /// WT-263. The plan-level ceiling on concurrently active rooms, backed by the new
    /// <c>subscription.plans.max_active_rooms</c> column (migration 050).
    ///
    /// A COLUMN, deliberately, not an entry in the <see cref="Features"/> JSON. Every hard quota the
    /// plan sells is already a typed column — <see cref="MaxParticipants"/>, <see cref="MaxLanguages"/>,
    /// <see cref="CreditsPerCycle"/> — while <c>features</c> is an opaque marketing bag
    /// (voice_clone_limit_mins, billing_model, external_integrations) that nothing validates and
    /// nothing indexes. Putting an enforced limit in there would recreate precisely the failure this
    /// ticket exists to end: an entitlement that each reader parses for itself. It would also be
    /// unreachable to plan CRUD validation, so an admin could save max_active_rooms = 0 and make
    /// every workspace on the plan unable to start a meeting, with no check anywhere.
    ///
    /// <c>allow_acl</c> is the counter-example already in the tree: with no column to read, the gRPC
    /// mapper mirrors ai_assistant_enabled and documents it as a stand-in. That is what a
    /// column-less entitlement costs.
    /// </summary>
    public int MaxActiveRooms { get; set; } = Constants.EntitlementConstants.PlatformDefaults.MaxActiveRooms;

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
