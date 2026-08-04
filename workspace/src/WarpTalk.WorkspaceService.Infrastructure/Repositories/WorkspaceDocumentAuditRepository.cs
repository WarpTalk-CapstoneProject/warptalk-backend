using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceDocumentAuditRepository
    : GenericRepository<WorkspaceDocumentAudit>, IWorkspaceDocumentAuditRepository
{
    public WorkspaceDocumentAuditRepository(WorkspaceDbContext context) : base(context)
    {
    }
}
