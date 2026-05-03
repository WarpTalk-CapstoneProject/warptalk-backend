using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.DTOs;

public class QuotaDeductResponse
{
    public Guid OwnerUserId { get; set; }

    public decimal PreviousBalance { get; set; }

    public decimal AddedCredits { get; set; }

    public decimal NewBalance { get; set; }

    public string? ReferenceId { get; set; }

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
}