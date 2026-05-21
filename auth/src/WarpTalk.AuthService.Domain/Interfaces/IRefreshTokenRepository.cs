using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IRefreshTokenRepository : IGenericRepository<RefreshToken>
{
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);
}
