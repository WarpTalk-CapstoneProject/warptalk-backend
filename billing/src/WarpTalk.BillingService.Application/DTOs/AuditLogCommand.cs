using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.DTOs;

public class AuditLogCommand
{
    public Guid WorkspaceId { get; set; }

    public string Action { get; set; } = default!;

    public decimal Amount { get; set; }

    public string? ReferenceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
