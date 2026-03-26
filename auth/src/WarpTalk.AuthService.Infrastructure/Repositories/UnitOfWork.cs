using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly AuthDbContext _db;

    public UnitOfWork(AuthDbContext db)
    {
        _db = db;
        Users = new GenericRepository<User>(db);
        Roles = new GenericRepository<Role>(db);
        Permissions = new GenericRepository<Permission>(db);
        UserRoles = new GenericRepository<UserRole>(db);
        UserSettings = new GenericRepository<UserSetting>(db);
        RefreshTokens = new GenericRepository<RefreshToken>(db);
        Workspaces = new GenericRepository<Workspace>(db);
        WorkspaceInvitations = new GenericRepository<WorkspaceInvitation>(db);
    }

    public IGenericRepository<User> Users { get; }
    public IGenericRepository<Role> Roles { get; }
    public IGenericRepository<Permission> Permissions { get; }
    public IGenericRepository<UserRole> UserRoles { get; }
    public IGenericRepository<UserSetting> UserSettings { get; }
    public IGenericRepository<RefreshToken> RefreshTokens { get; }
    public IGenericRepository<Workspace> Workspaces { get; }
    public IGenericRepository<WorkspaceInvitation> WorkspaceInvitations { get; }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);

    public void Dispose() => _db.Dispose();
}
