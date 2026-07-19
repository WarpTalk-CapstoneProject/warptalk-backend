using System;
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

    public TokenService(
        IUnitOfWork unitOfWork,
        IJwtTokenGenerator jwtGenerator,
        IOptions<AuthSettings> authSettings,
        ILogger<TokenService> logger)
    {
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

            var response = await AuthResponseHelper.CreateAuthResponseAsync(user, request.IpAddress, request.DeviceInfo, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct, storedToken.FamilyId);
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

            if (storedToken is not null && storedToken.UserId == userId)
            {
                storedToken.RevokedAt = DateTime.UtcNow;
                _refreshTokenRepository.Update(storedToken);
                await _unitOfWork.SaveChangesAsync(ct);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during logout. UserId: {UserId}", userId);
            return Result.Failure("An unexpected error occurred during logout.", ErrorCodes.InternalServerError);
        }
    }
}
