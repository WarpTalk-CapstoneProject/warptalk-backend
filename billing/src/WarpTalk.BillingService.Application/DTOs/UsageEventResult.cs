using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.DTOs;

public class UsageEventResult
{
    public Guid EventId { get; set; }

    public bool Success { get; set; }

    public decimal RemainingBalance { get; set; }

    public string Message { get; set; } = default!;

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    public decimal Credits { get; set; }

}