using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class WorkspaceMemberRepository : GenericRepository<WorkspaceMember>, IWorkspaceMemberRepository
{
    public WorkspaceMemberRepository(AuthDbContext db) : base(db)
    {
    }
}
