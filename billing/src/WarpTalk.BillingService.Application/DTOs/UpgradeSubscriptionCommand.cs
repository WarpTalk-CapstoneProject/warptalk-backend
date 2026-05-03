using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.DTOs;

public class UpgradeSubscriptionCommand
{
    public Guid SubscriptionId { get; set; }

    public Guid NewPlanId { get; set; }

    public Guid OwnerUserId { get; set; }

    public DateTime UpgradedAt { get; set; } = DateTime.UtcNow;
}