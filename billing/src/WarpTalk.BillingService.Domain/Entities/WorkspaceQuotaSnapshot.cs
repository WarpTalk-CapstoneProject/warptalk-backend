// =======================================================
// Domain/Entities/WorkspaceQuotaSnapshot.cs
// =======================================================

using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class WorkspaceQuotaSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// Fast-read cached balance
    /// Source of truth is CreditLedgerEntry
    /// </summary>
    public decimal CurrentBalance { get; set; }

    public decimal ReservedCredits { get; set; }

    /// <summary>
    /// Trigger low-credit alerts
    /// </summary>
    public decimal LowCreditThreshold { get; set; }

    public QuotaMode CurrentMode { get; set; }

    public DateTime UpdatedAt { get; set; }
        = DateTime.UtcNow;
}