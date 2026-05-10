using System;
using System.Collections.Generic;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Subscription
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PlanId { get; set; }

    public SubscriptionStatus Status { get; set; }

    public int CurrentTokens { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public DateTime CreatedAt { get; set; }

    public string Duration { get; set; } = "1mo"; // 1mo, 6mo, 1yr

    public string Tier { get; set; } = "Premium"; // Premium, Enterprise

    public virtual Plan Plan { get; set; } = null!;

    public virtual ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
