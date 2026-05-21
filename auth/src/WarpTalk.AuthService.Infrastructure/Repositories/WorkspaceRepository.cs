using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class WorkspaceRepository : GenericRepository<Workspace>, IWorkspaceRepository
{
    public WorkspaceRepository(AuthDbContext db) : base(db)
    {
    }
}
