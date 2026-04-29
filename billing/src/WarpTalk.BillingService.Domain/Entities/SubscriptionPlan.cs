using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// Subscription plan details
/// </summary>
/// <example>
/// {
///   "id": "22222222-2222-2222-2222-222222222222",
///   "name": "Pro",
///   "baseQuotaMinutes": 500,
///   "priceVnd": 199000,
///   "maxParticipants": 25,
///   "featuresJson": "{\"advancedTranslation\": true}",
///   "isActive": true
/// }
/// </example>
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
