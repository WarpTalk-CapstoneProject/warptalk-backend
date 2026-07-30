using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IUserRepository : IGenericRepository<User>
{
    Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> GetByEmailWithRolesAsync(string email, CancellationToken ct = default);
    Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByGoogleIdWithRolesAsync(string googleId, CancellationToken ct = default);
    Task<User?> GetByEmailVerificationTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<User?> GetByPasswordResetTokenHashAsync(string tokenHash, CancellationToken ct = default);
}
