using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

/// <summary>
/// Document approval/ingestion audit rows.
///
/// Unlike <see cref="IAdminAuditLogRepository"/> this one does extend the generic contract: the
/// admin audit log is declared append-only and has a test enforcing that, whereas nothing states
/// the same constraint here. Callers only append and read today — if that becomes a rule, narrow
/// this the same way rather than relying on convention.
/// </summary>
public interface IWorkspaceDocumentAuditRepository : IGenericRepository<WorkspaceDocumentAudit>
{
}
