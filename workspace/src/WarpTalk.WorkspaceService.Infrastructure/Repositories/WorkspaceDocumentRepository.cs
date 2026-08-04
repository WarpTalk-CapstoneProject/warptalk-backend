using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceDocumentRepository : GenericRepository<WorkspaceDocument>, IWorkspaceDocumentRepository
{
    public WorkspaceDocumentRepository(WorkspaceDbContext context) : base(context)
    {
    }
}
