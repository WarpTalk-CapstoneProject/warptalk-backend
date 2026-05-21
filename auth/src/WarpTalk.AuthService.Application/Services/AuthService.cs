using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

public class AuthService : IAuthService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtGenerator;
    private readonly IGoogleAuthService _googleAuthService;
    private readonly IDistributedCache _cache;
    private readonly AuthSettings _authSettings;
    private readonly TimeSpan _lockoutDuration;

    public AuthService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtGenerator,
        IGoogleAuthService googleAuthService,
        IDistributedCache cache,
        IOptions<AuthSettings> authSettings)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _jwtGenerator = jwtGenerator;
        _googleAuthService = googleAuthService;
        _cache = cache;
        _authSettings = authSettings.Value;
        _lockoutDuration = TimeSpan.FromMinutes(_authSettings.LockoutDurationMinutes);
        _userRepository = _unitOfWork.UserRepository;
        _refreshTokenRepository = _unitOfWork.RefreshTokenRepository;
    }

    public async Task<Result<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (await _userRepository.ExistsByEmailAsync(request.Email.ToLowerInvariant().Trim(), ct))
            return Result.Failure<AuthResponse>(AuthConstants.ErrorEmailExists, ErrorCodes.EmailExists);

        var passwordHash = _passwordHasher.Hash(request.Password);
        var user = AuthMapper.ToUser(request, passwordHash);

        await _userRepository.AddAsync(user, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var response = await AuthResponseHelper.CreateAuthResponseAsync(user, null, null, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct);
        return Result.Success(response);
    }

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = request.Email.ToLowerInvariant().Trim();
        var user = await _userRepository.GetByEmailWithRolesAsync(email, ct);
        if (user is null || user.DeletedAt is not null)
            return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidCredentials, ErrorCodes.InvalidCredentials);

        if (user.LockedUntil.HasValue && user.LockedUntil.Value <= DateTime.UtcNow)
        {
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            user.IsLocked = false;
            _userRepository.Update(user);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        var statusResult = UserStatusHelper.CheckUserStatus<AuthResponse>(user);
        if (statusResult is not null)
            return statusResult;

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= _authSettings.MaxFailedAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.Add(_lockoutDuration);
            }
            _userRepository.Update(user);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidCredentials, ErrorCodes.InvalidCredentials);
        }

        // Reset lockout on successful login
        user.FailedLoginAttempts = 0;
        user.IsLocked = false;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginIp = request.IpAddress;
        user.UpdatedAt = DateTime.UtcNow;
        _userRepository.Update(user);

        var response = await AuthResponseHelper.CreateAuthResponseAsync(user, request.IpAddress, request.DeviceInfo, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct);
        return Result.Success(response);
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

    public async Task<Result<UserDto>> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdWithRolesAsync(userId, ct);
        if (user is null)
            return Result.Failure<UserDto>(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

        return Result.Success(AuthMapper.ToDto(user, _authSettings.DefaultRole));
    }

    public async Task<Result<UserDto>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdWithRolesAsync(userId, ct);
        if (user is null)
            return Result.Failure<UserDto>(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

        if (request.FullName is not null) user.FullName = request.FullName.Trim();
        if (request.Phone is not null) user.Phone = request.Phone.Trim();
        if (request.PreferredLanguage is not null) user.PreferredLanguage = request.PreferredLanguage;
        if (request.Timezone is not null) user.Timezone = request.Timezone;
        user.UpdatedAt = DateTime.UtcNow;

        _userRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(AuthMapper.ToDto(user, _authSettings.DefaultRole));
    }

    public async Task<Result<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, CancellationToken ct = default)
    {
        var payload = await _googleAuthService.VerifyGoogleTokenAsync(request.IdToken, ct);
        if (payload is null)
            return Result.Failure<AuthResponse>(AuthConstants.ErrorGoogleTokenInvalid, ErrorCodes.InvalidToken);

        var email = payload.Email.ToLowerInvariant().Trim();
        var user = await _userRepository.GetByEmailWithRolesAsync(email, ct);

        if (user is null)
        {
            user = AuthMapper.ToUser(payload);
            await _userRepository.AddAsync(user, ct);
        }
        else
        {
            if (user.DeletedAt is not null)
                return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidCredentials, ErrorCodes.InvalidCredentials);

            bool needsUpdate = false;

            if (user.GoogleId == null)
            {
                user.GoogleId = payload.Subject;
                if (string.IsNullOrEmpty(user.AvatarUrl)) user.AvatarUrl = payload.Picture;
                needsUpdate = true;
            }

            if (payload.EmailVerified && !user.EmailVerified)
            {
                user.EmailVerified = true;
                user.EmailVerifiedAt = DateTime.UtcNow;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                _userRepository.Update(user);
            }
        }

        var statusResult = UserStatusHelper.CheckUserStatus<AuthResponse>(user);
        if (statusResult is not null)
            return statusResult;

        user.FailedLoginAttempts = 0;
        user.IsLocked = false;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginIp = request.IpAddress;

        if (user.Id != Guid.Empty) _userRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);

        var response = await AuthResponseHelper.CreateAuthResponseAsync(user, request.IpAddress, request.DeviceInfo, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct);
        return Result.Success(response);
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

        // allow empty PasswordHash if user was created via Google and has no standard password yet
        if (!string.IsNullOrEmpty(user.PasswordHash) && !_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Result.Failure(AuthConstants.ErrorInvalidPassword, ErrorCodes.InvalidPassword);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        _userRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }







    public async Task<Result> ResendVerificationAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null || user.DeletedAt is not null)
            return Result.Failure(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

        if (UserStatusHelper.GetAccountStatus(user) == AccountStatus.DISABLED)
            return Result.Failure(AuthConstants.ErrorAccountInactive, ErrorCodes.AccountInactive);

        if (user.EmailVerified)
            return Result.Failure("Email is already verified", ErrorCodes.InvalidState);

        // 1. Check rate limit window (Max 5 requests per 15 minutes)
        var windowKey = $"resend:window:{userId}";
        var attemptsString = await _cache.GetStringAsync(windowKey, ct);
        int attemptsCount = string.IsNullOrEmpty(attemptsString) ? 0 : int.Parse(attemptsString);

        if (attemptsCount >= 5)
        {
            return Result.Failure(AuthConstants.ErrorRateLimitExceeded, ErrorCodes.RateLimitExceeded);
        }

        // 2. Check 60-second cooldown
        var cooldownKey = $"resend:cooldown:{userId}";
        var cooldownString = await _cache.GetStringAsync(cooldownKey, ct);
        if (!string.IsNullOrEmpty(cooldownString))
        {
            return Result.Failure(AuthConstants.ErrorCooldownActive, ErrorCodes.CooldownActive);
        }

        // 3. Set cooldown and update attempts in cache
        var cooldownOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) };
        await _cache.SetStringAsync(cooldownKey, "1", cooldownOptions, ct);

        var windowOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) };
        await _cache.SetStringAsync(windowKey, (attemptsCount + 1).ToString(), windowOptions, ct);

        // Simulate dispatching a verification email
        Console.WriteLine($"[Verification] Generated verification token and dispatched verification email for user: {user.Email}");

        return Result.Success();
    }


}
