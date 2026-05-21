using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class WorkspaceInvitationRepository : GenericRepository<WorkspaceInvitation>, IWorkspaceInvitationRepository
{
    public WorkspaceInvitationRepository(AuthDbContext db) : base(db)
    {
    }
}
