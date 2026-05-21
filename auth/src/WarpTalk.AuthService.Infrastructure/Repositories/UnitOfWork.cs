using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly AuthDbContext _context;
    private readonly Dictionary<Type, object> _repositories = new();

    public UnitOfWork(
        AuthDbContext context,
        IUserRepository userRepository,
        IRoleRepository roleRepository,
        IPermissionRepository permissionRepository,
        IUserRoleRepository userRoleRepository,
        IUserSettingRepository userSettingRepository,
        IRefreshTokenRepository refreshTokenRepository,
        IWorkspaceRepository workspaceRepository,
        IWorkspaceInvitationRepository workspaceInvitationRepository,
        IWorkspaceMemberRepository workspaceMemberRepository)
    {
        _context = context;
        UserRepository = userRepository;
        RoleRepository = roleRepository;
        PermissionRepository = permissionRepository;
        UserRoleRepository = userRoleRepository;
        UserSettingRepository = userSettingRepository;
        RefreshTokenRepository = refreshTokenRepository;
        WorkspaceRepository = workspaceRepository;
        WorkspaceInvitationRepository = workspaceInvitationRepository;
        WorkspaceMemberRepository = workspaceMemberRepository;
    }

    public IUserRepository UserRepository { get; }
    public IRoleRepository RoleRepository { get; }
    public IPermissionRepository PermissionRepository { get; }
    public IUserRoleRepository UserRoleRepository { get; }
    public IUserSettingRepository UserSettingRepository { get; }
    public IRefreshTokenRepository RefreshTokenRepository { get; }
    public IWorkspaceRepository WorkspaceRepository { get; }
    public IWorkspaceInvitationRepository WorkspaceInvitationRepository { get; }
    public IWorkspaceMemberRepository WorkspaceMemberRepository { get; }

    public IGenericRepository<T> Repository<T>() where T : class
    {
        var type = typeof(T);
        if (!_repositories.ContainsKey(type))
        {
            var repositoryInstance = new GenericRepository<T>(_context);
            _repositories.Add(type, repositoryInstance);
        }
        return (IGenericRepository<T>)_repositories[type];
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);

    public void Dispose() => _context.Dispose();
}
