using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Plan
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public string Tier { get; set; } = null!;

    public decimal Price { get; set; }

    public string Currency { get; set; } = "VND";

    public string BillingCycle { get; set; } = "monthly";

    public int CreditsPerCycle { get; set; }



    public int MaxParticipants { get; set; } = 2;

    public int MaxLanguages { get; set; } = 2;

    public bool VoiceCloneEnabled { get; set; }

    public bool AiAssistantEnabled { get; set; }

    public bool GlossaryEnabled { get; set; }

    public bool DedicatedGpu { get; set; }

    public string Features { get; set; } = "{}";

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
