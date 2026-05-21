using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IUserRepository UserRepository { get; }
    IRoleRepository RoleRepository { get; }
    IPermissionRepository PermissionRepository { get; }
    IUserRoleRepository UserRoleRepository { get; }
    IUserSettingRepository UserSettingRepository { get; }
    IRefreshTokenRepository RefreshTokenRepository { get; }
    IWorkspaceRepository WorkspaceRepository { get; }
    IWorkspaceInvitationRepository WorkspaceInvitationRepository { get; }
    IWorkspaceMemberRepository WorkspaceMemberRepository { get; }
    IGenericRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
