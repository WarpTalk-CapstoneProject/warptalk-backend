using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

/// <summary>
/// Persistence access to workspace documents. Adds nothing to the generic contract yet — it
/// exists so callers depend on a document-shaped seam rather than on IGenericRepository
/// directly, which is where document-specific queries land as they appear.
/// </summary>
public interface IWorkspaceDocumentRepository : IGenericRepository<WorkspaceDocument>
{
}
