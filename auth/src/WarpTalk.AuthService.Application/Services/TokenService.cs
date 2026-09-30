using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

public class TokenService : ITokenService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IJwtTokenGenerator _jwtGenerator;
    private readonly AuthSettings _authSettings;
    private readonly ILogger<TokenService> _logger;

    // G10: optional so the many hand-built test instances keep compiling; DI always supplies it.
    private readonly IStaffAccessService? _staffAccess;

    public TokenService(
        IUnitOfWork unitOfWork,
        IJwtTokenGenerator jwtGenerator,
        IOptions<AuthSettings> authSettings,
        ILogger<TokenService> logger,
        IStaffAccessService? staffAccess = null)
    {
        _staffAccess = staffAccess;
        _unitOfWork = unitOfWork;
        _jwtGenerator = jwtGenerator;
        _authSettings = authSettings.Value;
        _logger = logger;
        _userRepository = _unitOfWork.UserRepository;
        _refreshTokenRepository = _unitOfWork.RefreshTokenRepository;
    }

    public async Task<Result<AuthResponse>> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
                return Result.Failure<AuthResponse>("Refresh token is required.", ErrorCodes.ValidationError);

            var tokenHash = TokenHasher.Hash(request.RefreshToken);
            var storedToken = await _refreshTokenRepository.GetByTokenHashAsync(tokenHash, ct);

            if (storedToken is null)
                return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidToken, ErrorCodes.InvalidToken);

            if (storedToken.RevokedAt is not null)
            {
                // This token was already rotated out once before — presenting it again means
                // either a replay/race, or someone else has a copy of a stolen token. Either
                // way, the whole rotation family (every descendant of the original login) gets
                // revoked so a stolen token can't be used to keep the session alive.
                await _refreshTokenRepository.RevokeFamilyAsync(storedToken.FamilyId, ct);
                await _unitOfWork.SaveChangesAsync(ct);
                _logger.LogWarning("Refresh token reuse detected for family {FamilyId}; family revoked.", storedToken.FamilyId);
                return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidToken, ErrorCodes.InvalidToken);
            }

            if (storedToken.ExpiresAt < DateTime.UtcNow)
                return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidToken, ErrorCodes.InvalidToken);

            var user = await _userRepository.GetByIdWithRolesAsync(storedToken.UserId, ct);
            if (user is null || user.DeletedAt is not null)
                return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidCredentials, ErrorCodes.InvalidCredentials);

            var statusResult = UserStatusHelper.CheckUserStatus<AuthResponse>(user);
            if (statusResult is not null)
                return statusResult;

            // Only burn the presented token once we know the refresh will actually succeed —
            // a status/validity failure above must leave it usable so the caller can retry
            // (e.g. after verifying their email) instead of being locked out by this attempt.
            storedToken.RevokedAt = DateTime.UtcNow;
            _refreshTokenRepository.Update(storedToken);

            var response = await AuthResponseHelper.CreateAuthResponseAsync(user, request.IpAddress, request.DeviceInfo, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct, storedToken.FamilyId, staffAccess: _staffAccess);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while refreshing token.");
            return Result.Failure<AuthResponse>("An unexpected error occurred while refreshing the token.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> LogoutAsync(Guid userId, string refreshToken, CancellationToken ct = default)
    {
        try
        {
            var tokenHash = TokenHasher.Hash(refreshToken);
            var storedToken = await _refreshTokenRepository.GetByTokenHashAsync(tokenHash, ct);

            if (storedToken is null)
            {
                // Nothing to revoke. Still a success: logout is idempotent, and telling the
                // caller whether a token exists would make this endpoint an oracle.
                _logger.LogInformation(
                    "Logout presented an unknown refresh token. UserId: {UserId}", userId);
                return Result.Success();
            }

            if (storedToken.UserId != userId)
            {
                // The presented token belongs to somebody else. It is NOT revoked — otherwise
                // any authenticated user holding another user's refresh token could terminate
                // their session. There is no legitimate way to reach this branch, so it is an
                // attack signal and gets logged as one.
                _logger.LogWarning(
                    "Logout presented a refresh token owned by a different user. "
                    + "Caller: {UserId}. Token was not revoked.",
                    userId);
                return Result.Success();
            }

            // Revoke the whole rotation family, not just the presented leaf.
            //
            // A family is one login lineage: the token issued at sign-in and every token that
            // rotation has since derived from it. Revoking only the leaf ended the session on
            // paper while leaving the lineage intact, so "log out" did less than the name
            // promises. RefreshTokenAsync already revokes by family when it detects reuse; a
            // deliberate logout should be at least as thorough as reuse detection.
            await _refreshTokenRepository.RevokeFamilyAsync(storedToken.FamilyId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Logout revoked refresh token family {FamilyId}. UserId: {UserId}",
                storedToken.FamilyId,
                userId);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during logout. UserId: {UserId}", userId);
            return Result.Failure("An unexpected error occurred during logout.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<IReadOnlyList<UserSessionDto>>> GetSessionsAsync(
        Guid userId,
        string? currentRefreshToken,
        CancellationToken ct = default)
    {
        try
        {
            var currentFamilyId = await ResolveCurrentFamilyAsync(userId, currentRefreshToken, ct);
            var rows = await _refreshTokenRepository.GetActiveSessionsForUserAsync(userId, ct);

            IReadOnlyList<UserSessionDto> sessions = rows
                .Select(r => new UserSessionDto(
                    r.FamilyId,
                    r.DeviceInfo,
                    r.IpAddress,
                    r.SignedInAt,
                    r.LastActiveAt,
                    r.ExpiresAt,
                    r.FamilyId == currentFamilyId))
                // The device in your hand first; the rest by recency, as the repository ordered them.
                .OrderByDescending(s => s.IsCurrent)
                .ToList();

            return Result.Success(sessions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing sessions. UserId: {UserId}", userId);
            return Result.Failure<IReadOnlyList<UserSessionDto>>(
                "An unexpected error occurred while loading your sessions.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RevokeSessionAsync(
        Guid userId,
        Guid sessionId,
        string? currentRefreshToken,
        CancellationToken ct = default)
    {
        try
        {
            // Ownership is checked by reading the caller's OWN live sessions, never by looking the
            // id up globally: another user's session id, a dead one and a made-up one are all the
            // same answer, so this endpoint cannot be used to learn which ids exist.
            var sessions = await _refreshTokenRepository.GetActiveSessionsForUserAsync(userId, ct);
            if (!sessions.Any(s => s.FamilyId == sessionId))
                return Result.Failure("Session not found.", ErrorCodes.NotFound);

            var currentFamilyId = await ResolveCurrentFamilyAsync(userId, currentRefreshToken, ct);
            if (currentFamilyId == sessionId)
            {
                // Revoking the session in your own hand from here would leave this browser holding
                // cookies for a dead family and discovering it on the next refresh — the dead-session
                // path, which is exactly where the WT-405 logout storm lived. Logout clears the
                // cookies in the same response that revokes the family; that is the way out.
                return Result.Failure(
                    "This is the session you are using. Sign out instead.", ErrorCodes.Conflict);
            }

            // The same revocation logout performs: the whole rotation family.
            await _refreshTokenRepository.RevokeFamilyAsync(sessionId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation(
                "User revoked session family {FamilyId}. UserId: {UserId}", sessionId, userId);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while revoking a session. UserId: {UserId}", userId);
            return Result.Failure("An unexpected error occurred while signing out that session.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RevokeOtherSessionsAsync(
        Guid userId,
        string? currentRefreshToken,
        CancellationToken ct = default)
    {
        try
        {
            var currentFamilyId = await ResolveCurrentFamilyAsync(userId, currentRefreshToken, ct);
            if (currentFamilyId is null)
            {
                // Without knowing which session is "this one", "all others" would have to mean
                // "all", and signing the caller out of the device they pressed the button on is
                // not what they asked for.
                return Result.Failure(
                    "Could not identify this session. Sign in again, then retry.", ErrorCodes.ValidationError);
            }

            await _refreshTokenRepository.RevokeAllForUserExceptFamilyAsync(userId, currentFamilyId.Value, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation(
                "User revoked all sessions except family {FamilyId}. UserId: {UserId}", currentFamilyId, userId);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while revoking other sessions. UserId: {UserId}", userId);
            return Result.Failure("An unexpected error occurred while signing out other sessions.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Which family the request's refresh token belongs to — null when there is no token, it is
    /// unknown, or it belongs to someone else.
    ///
    /// The access token carries no link to its refresh token, and this is deliberately not solved
    /// by adding one: a new JWT claim would only exist on tokens minted after the deploy, and the
    /// refresh cookie already reaches every /api/v1/auth path (its cookie Path). A rotated-out
    /// leaf still names its family, so a refresh racing this request does not lose "This device".
    /// </summary>
    private async Task<Guid?> ResolveCurrentFamilyAsync(Guid userId, string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        var stored = await _refreshTokenRepository.GetByTokenHashAsync(TokenHasher.Hash(refreshToken), ct);
        return stored is not null && stored.UserId == userId ? stored.FamilyId : null;
    }
}
