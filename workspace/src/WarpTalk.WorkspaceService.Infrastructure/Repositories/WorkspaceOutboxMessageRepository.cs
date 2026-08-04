using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceOutboxMessageRepository
    : GenericRepository<WorkspaceOutboxMessage>, IWorkspaceOutboxMessageRepository
{
    public WorkspaceOutboxMessageRepository(WorkspaceDbContext context) : base(context)
    {
    }
}
