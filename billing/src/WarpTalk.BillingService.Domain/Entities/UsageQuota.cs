using System;

namespace WarpTalk.BillingService.Domain.Entities;

public class UsageQuota
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid WorkspaceId { get; set; }
    public Guid PlanId { get; set; }
    
    public decimal TotalAllocatedMinutes { get; set; }
    public decimal ConsumedMinutes { get; set; }
    
    // Computed property for easy access, mapped to DB or evaluated in memory
    public decimal RemainingMinutes => TotalAllocatedMinutes - ConsumedMinutes;
    
    public DateTime CycleStartDate { get; set; }
    public DateTime CycleEndDate { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    
    // Navigation property
    public SubscriptionPlan Plan { get; set; } = null!;
}

