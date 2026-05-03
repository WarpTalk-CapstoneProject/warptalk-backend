// =======================================================
// Domain/Entities/QuotaAuditLog.cs
// =======================================================

using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class QuotaAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? MeetingId { get; set; }

    public Guid? RelatedLedgerEntryId { get; set; }

    public AuditAction Action { get; set; }

    public string Description { get; set; }
        = string.Empty;

    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; }
        = DateTime.UtcNow;
}