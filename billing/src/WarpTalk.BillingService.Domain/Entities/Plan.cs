using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Plan
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public decimal PricePerMonth { get; set; }

    public int TokensPerMonth { get; set; }

    public virtual ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
}
