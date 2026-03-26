using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IGenericRepository<User> Users { get; }
    IGenericRepository<Role> Roles { get; }
    IGenericRepository<Permission> Permissions { get; }
    IGenericRepository<UserRole> UserRoles { get; }
    IGenericRepository<UserSetting> UserSettings { get; }
    IGenericRepository<RefreshToken> RefreshTokens { get; }
    IGenericRepository<Workspace> Workspaces { get; }
    IGenericRepository<WorkspaceInvitation> WorkspaceInvitations { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
