using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.DTOs;

public class StartSessionCommand
{
    public Guid MeetingId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid HostUserId { get; set; } 

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}
