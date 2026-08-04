using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceVerifiedDomainRepository
    : GenericRepository<WorkspaceVerifiedDomain>, IWorkspaceVerifiedDomainRepository
{
    public WorkspaceVerifiedDomainRepository(WorkspaceDbContext context) : base(context)
    {
    }
}
