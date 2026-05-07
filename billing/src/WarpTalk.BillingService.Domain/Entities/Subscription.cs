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

    public int CurrentCredits { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public DateTime CreatedAt { get; set; }



    public virtual Plan Plan { get; set; } = null!;

    public virtual ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
