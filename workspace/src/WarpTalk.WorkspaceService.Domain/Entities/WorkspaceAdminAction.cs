using System;

namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>
/// One administrative action recorded in the platform audit log. Rows are append-only:
/// reactivating never rewrites the suspend row, so the reason history survives (WT-204).
///
/// Generalized by WT-210 to hold actions from every service, not just workspace lifecycle —
/// other services publish admin.action_recorded and the workspace service appends here,
/// because each service owns a separate logical database.
/// </summary>
public partial class WorkspaceAdminAction
{
    public Guid Id { get; set; }

    /// <summary>Which service performed the action, e.g. "workspace-service".</summary>
    public string SourceService { get; set; } = null!;

    /// <summary>One of <see cref="Constants.WorkspaceAdminActionTypes"/> or a source-defined action.</summary>
    public string Action { get; set; } = null!;

    /// <summary>Subject type — see WarpTalk.Shared.Events.AdminAuditEntityTypes.</summary>
    public string EntityType { get; set; } = null!;

    /// <summary>Subject id. For workspace actions this equals <see cref="WorkspaceId"/>.</summary>
    public Guid? EntityId { get; set; }

    /// <summary>Null for platform-wide actions such as a pricing change.</summary>
    public Guid? WorkspaceId { get; set; }

    public string Reason { get; set; } = null!;

    /// <summary>"succeeded" or "failed" — a rejected admin attempt is still attributable.</summary>
    public string Result { get; set; } = null!;

    /// <summary>The system admin who performed the action, taken from authenticated claims.</summary>
    public Guid PerformedBy { get; set; }

    public DateTime PerformedAt { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>Redacted JSON summary of prior state, or null when not applicable.</summary>
    public string? BeforeSummary { get; set; }

    /// <summary>Redacted JSON summary of resulting state, or null when not applicable.</summary>
    public string? AfterSummary { get; set; }
}
