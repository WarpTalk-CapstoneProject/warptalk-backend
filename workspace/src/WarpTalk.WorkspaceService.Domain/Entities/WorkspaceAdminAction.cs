using System;

namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>
/// One system-admin lifecycle action taken against a workspace. Rows are append-only:
/// reactivating never rewrites the suspend row, so the reason history survives (WT-204).
/// </summary>
public partial class WorkspaceAdminAction
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>One of <see cref="Constants.WorkspaceAdminActionTypes"/>.</summary>
    public string Action { get; set; } = null!;

    public string Reason { get; set; } = null!;

    /// <summary>The system admin who performed the action, taken from authenticated claims.</summary>
    public Guid PerformedBy { get; set; }

    public DateTime PerformedAt { get; set; }

    public string? CorrelationId { get; set; }
}
