using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.DTOs;

public class UsageEventCommand
{
    public Guid WorkspaceId { get; set; }

    public Guid? MeetingId { get; set; }

    public string FeatureType { get; set; } = default!;

    public decimal Credits { get; set; }

    public string IdempotencyKey { get; set; } = default!;
}