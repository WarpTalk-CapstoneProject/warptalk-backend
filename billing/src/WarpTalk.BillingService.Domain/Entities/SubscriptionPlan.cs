using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class SubscriptionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PlanType Name { get; set; }
    public decimal BaseQuotaMinutes { get; set; }
    public decimal PriceVnd { get; set; }
    public int MaxParticipants { get; set; }
    
    /// <summary>
    /// JSON string to store feature flags (e.g. advancedTranslation, premiumVoice)
    /// </summary>
    public string FeaturesJson { get; set; } = "{}";
    
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
