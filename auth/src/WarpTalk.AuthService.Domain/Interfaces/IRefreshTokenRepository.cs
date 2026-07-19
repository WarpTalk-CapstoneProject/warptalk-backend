using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IRefreshTokenRepository : IGenericRepository<RefreshToken>
{
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    // Revokes every non-revoked token in a rotation family — used when a rotated-out
    // (already-revoked) refresh token is presented again, signalling possible theft.
    Task RevokeFamilyAsync(Guid familyId, CancellationToken ct = default);
}
