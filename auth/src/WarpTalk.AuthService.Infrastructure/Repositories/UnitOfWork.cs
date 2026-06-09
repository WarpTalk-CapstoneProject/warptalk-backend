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
        IRefreshTokenRepository refreshTokenRepository)
    {
        _context = context;
        UserRepository = userRepository;
        RoleRepository = roleRepository;
        PermissionRepository = permissionRepository;
        UserRoleRepository = userRoleRepository;
        UserSettingRepository = userSettingRepository;
        RefreshTokenRepository = refreshTokenRepository;
    }

    public IUserRepository UserRepository { get; }
    public IRoleRepository RoleRepository { get; }
    public IPermissionRepository PermissionRepository { get; }
    public IUserRoleRepository UserRoleRepository { get; }
    public IUserSettingRepository UserSettingRepository { get; }
    public IRefreshTokenRepository RefreshTokenRepository { get; }

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

    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _currentTransaction;

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);

    public async Task BeginTransactionAsync(CancellationToken ct = default)
    {
        _currentTransaction = await _context.Database.BeginTransactionAsync(ct);
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.CommitAsync(ct);
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.RollbackAsync(ct);
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public void Dispose()
    {
        _currentTransaction?.Dispose();
        _context.Dispose();
    }
}
