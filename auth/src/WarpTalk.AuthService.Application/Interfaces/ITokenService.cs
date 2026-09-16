using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface ITokenService
{
    Task<Result<AuthResponse>> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken ct = default);
    Task<Result> LogoutAsync(Guid userId, string refreshToken, CancellationToken ct = default);

    /// <summary>The caller's live sessions. <paramref name="currentRefreshToken"/> (the request's refresh cookie, if any) marks which one is this device.</summary>
    Task<Result<IReadOnlyList<UserSessionDto>>> GetSessionsAsync(Guid userId, string? currentRefreshToken, CancellationToken ct = default);

    /// <summary>Ends one of the caller's own sessions. Not found for anyone else's; conflict for the current one, which ends through logout.</summary>
    Task<Result> RevokeSessionAsync(Guid userId, Guid sessionId, string? currentRefreshToken, CancellationToken ct = default);

    /// <summary>Ends every session the caller has except the one making this request.</summary>
    Task<Result> RevokeOtherSessionsAsync(Guid userId, string? currentRefreshToken, CancellationToken ct = default);
}
