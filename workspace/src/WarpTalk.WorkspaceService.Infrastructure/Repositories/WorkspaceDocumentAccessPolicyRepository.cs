using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceDocumentAccessPolicyRepository
    : GenericRepository<WorkspaceDocumentAccessPolicy>, IWorkspaceDocumentAccessPolicyRepository
{
    public WorkspaceDocumentAccessPolicyRepository(WorkspaceDbContext context) : base(context)
    {
    }
}
