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

    public TokenService(
        IUnitOfWork unitOfWork,
        IJwtTokenGenerator jwtGenerator,
        IOptions<AuthSettings> authSettings)
    {
        _unitOfWork = unitOfWork;
        _jwtGenerator = jwtGenerator;
        _authSettings = authSettings.Value;
        _userRepository = _unitOfWork.UserRepository;
        _refreshTokenRepository = _unitOfWork.RefreshTokenRepository;
    }

    public async Task<Result<AuthResponse>> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken ct = default)
    {
        var tokenHash = TokenHasher.Hash(request.RefreshToken);
        var storedToken = await _refreshTokenRepository.GetByTokenHashAsync(tokenHash, ct);

        if (storedToken is null || storedToken.RevokedAt is not null || storedToken.ExpiresAt < DateTime.UtcNow)
            return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidToken, ErrorCodes.InvalidToken);

        // Revoke old token
        storedToken.RevokedAt = DateTime.UtcNow;
        _refreshTokenRepository.Update(storedToken);

        var user = await _userRepository.GetByIdWithRolesAsync(storedToken.UserId, ct);
        if (user is null || user.DeletedAt is not null)
        {
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidCredentials, ErrorCodes.InvalidCredentials);
        }

        var statusResult = UserStatusHelper.CheckUserStatus<AuthResponse>(user);
        if (statusResult is not null)
        {
            await _unitOfWork.SaveChangesAsync(ct);
            return statusResult;
        }

        var response = await AuthResponseHelper.CreateAuthResponseAsync(user, request.IpAddress, request.DeviceInfo, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct);
        return Result.Success(response);
    }

    public async Task<Result> LogoutAsync(Guid userId, string refreshToken, CancellationToken ct = default)
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
}
