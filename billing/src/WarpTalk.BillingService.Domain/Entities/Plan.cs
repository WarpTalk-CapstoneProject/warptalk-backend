using System;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Plan
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public string Tier { get; set; } = null!;

    public decimal Price { get; set; }

    public string Currency { get; set; } = null!;

    public string BillingCycle { get; set; } = null!;

    public int CreditsPerCycle { get; set; }

    public int MaxParticipants { get; set; }

    public int MaxLanguages { get; set; }

    public bool VoiceCloneEnabled { get; set; }

    public bool AiAssistantEnabled { get; set; }

    public bool GlossaryEnabled { get; set; }

    public bool DedicatedGpu { get; set; }

    public int VoiceCloneLimitMins { get; set; }

    public bool AllowGlossary { get; set; }

    public bool AllowAcl { get; set; }

    public string Features { get; set; } = null!;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public virtual ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
}
