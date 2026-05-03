using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.DTOs;

public class CreateSubscriptionCommand
{
    public Guid WorkspaceId { get; set; }

    public Guid PlanId { get; set; }

    public Guid OwnerUserId { get; set; }

    public DateTime StartDate { get; set; } = DateTime.UtcNow;
    public int? DurationDays { get; set; }
}