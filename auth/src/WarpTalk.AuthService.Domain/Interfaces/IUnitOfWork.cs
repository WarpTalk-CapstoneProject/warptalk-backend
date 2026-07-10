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
    IVoiceProfileRepository VoiceProfileRepository { get; }
    IVoiceConsentRepository VoiceConsentRepository { get; }
    IVoiceSampleRepository VoiceSampleRepository { get; }
    IGenericRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
