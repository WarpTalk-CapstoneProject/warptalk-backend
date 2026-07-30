using System;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Domain.Extensions;

public static class WorkspaceVerifiedDomainExtensions
{
    /// <summary>
    /// Soft-revokes an active verified domain record.
    /// Encapsulates state mutation while keeping entity POCO scaffold-friendly.
    /// </summary>
    public static void SoftRevoke(this WorkspaceVerifiedDomain entry, Guid revokedByUserId, DateTime? utcNow = null)
    {
        if (entry == null || entry.RevokedAt != null)
            return;

        var now = utcNow ?? DateTime.UtcNow;
        entry.Status = VerifiedDomainStatus.Revoked.ToString().ToLower();
        entry.RevokedAt = now;
        entry.UpdatedAt = now;
        entry.UpdatedBy = revokedByUserId;
    }
}
