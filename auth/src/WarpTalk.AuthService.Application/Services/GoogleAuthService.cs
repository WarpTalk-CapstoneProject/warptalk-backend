using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Extensions;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

public class GoogleAuthService : IGoogleAuthService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IJwtTokenGenerator _jwtGenerator;
    private readonly IGoogleTokenVerifier _googleTokenVerifier;
    private readonly IDistributedCache _cache;
    private readonly AuthSettings _authSettings;
    private readonly ILogger<GoogleAuthService> _logger;

    public GoogleAuthService(
        IUnitOfWork unitOfWork,
        IJwtTokenGenerator jwtGenerator,
        IGoogleTokenVerifier googleTokenVerifier,
        IDistributedCache cache,
        IOptions<AuthSettings> authSettings,
        ILogger<GoogleAuthService> logger)
    {
        _unitOfWork = unitOfWork;
        _jwtGenerator = jwtGenerator;
        _googleTokenVerifier = googleTokenVerifier;
        _cache = cache;
        _authSettings = authSettings.Value;
        _logger = logger;
        _userRepository = _unitOfWork.UserRepository;
        _refreshTokenRepository = _unitOfWork.RefreshTokenRepository;
    }

    public async Task<Result<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, CancellationToken ct = default)
    {
        try
        {
            var payload = await _googleTokenVerifier.VerifyGoogleTokenAsync(request.IdToken, ct);
            if (payload is null)
                return Result.Failure<AuthResponse>(AuthConstants.ErrorGoogleTokenInvalid, ErrorCodes.InvalidToken);

            var email = payload.Email.ToLowerInvariant().Trim();
            var user = await _userRepository.GetByEmailWithRolesAsync(email, ct);

            if (user is null)
            {
                user = UserMapper.ToUser(payload);
                await _userRepository.AddAsync(user, ct);

                // Provision default settings for new Google OAuth sign-in
                var settings = UserSettingsMapper.CreateDefaultUserSettings(user.Id);
                _unitOfWork.UserSettingRepository.Add(settings);

                await _unitOfWork.SaveChangesAsync(ct);
            }
            else
            {
                if (user.DeletedAt is not null)
                    return Result.Failure<AuthResponse>(AuthConstants.ErrorInvalidCredentials, ErrorCodes.InvalidCredentials);

                bool needsUpdate = false;

                if (user.GoogleId == null)
                {
                    // Safe Matching Rule: If matched account exists but EmailVerified is false, block automatic linking to prevent takeover.
                    if (!user.EmailVerified && !string.IsNullOrEmpty(user.PasswordHash))
                    {
                        await TriggerVerificationEmailAsync(user, ct);
                        return Result.Failure<AuthResponse>(AuthConstants.ErrorEmailNotVerified, ErrorCodes.EmailNotVerified);
                    }

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
                    await _unitOfWork.SaveChangesAsync(ct);
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

            _userRepository.Update(user);

            await _unitOfWork.SaveChangesAsync(ct);

            var response = await AuthResponseHelper.CreateAuthResponseAsync(user, request.IpAddress, request.DeviceInfo, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during Google login.");
            return Result.Failure<AuthResponse>("An unexpected error occurred during Google login.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> LinkGoogleAsync(Guid userId, LinkGoogleRequest request, CancellationToken ct = default)
    {
        try
        {
            var payload = await _googleTokenVerifier.VerifyGoogleTokenAsync(request.IdToken, ct);
            if (payload is null)
                return Result.Failure(AuthConstants.ErrorGoogleTokenInvalid, ErrorCodes.InvalidToken);

            var user = await _userRepository.GetByIdAsync(userId, ct);
            if (user is null)
                return Result.Failure(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

            if (!string.Equals(user.Email, payload.Email, StringComparison.OrdinalIgnoreCase))
                return Result.Failure(AuthConstants.ErrorGoogleEmailMismatch, ErrorCodes.InvalidState);

            user.GoogleId = payload.Subject;
            if (payload.EmailVerified && !user.EmailVerified)
            {
                user.EmailVerified = true;
                user.EmailVerifiedAt = DateTime.UtcNow;
            }

            if (string.IsNullOrEmpty(user.AvatarUrl))
            {
                user.AvatarUrl = payload.Picture;
            }

            _userRepository.Update(user);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while linking Google account. UserId: {UserId}", userId);
            return Result.Failure("An unexpected error occurred while linking Google account.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UnlinkGoogleAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId, ct);
            if (user is null)
                return Result.Failure(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

            if (string.IsNullOrEmpty(user.PasswordHash))
                return Result.Failure(AuthConstants.ErrorUnlinkGoogleNoPassword, ErrorCodes.MinAuthMethodRequired);

            user.GoogleId = null;
            _userRepository.Update(user);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while unlinking Google account. UserId: {UserId}", userId);
            return Result.Failure("An unexpected error occurred while unlinking Google account.", ErrorCodes.InternalServerError);
        }
    }

    private async Task TriggerVerificationEmailAsync(User user, CancellationToken ct)
    {
        try
        {
            // Check rate limit window (Max 5 requests per 15 minutes) - Fixed Window
            var windowKey = $"resend:window:{user.Id}";
            var attemptsString = await _cache.GetStringAsync(windowKey, ct);
            
            int attemptsCount = 0;
            DateTime expiryTime = DateTime.UtcNow.AddMinutes(15);
            if (!string.IsNullOrEmpty(attemptsString))
            {
                var parts = attemptsString.Split('|');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var parsedCount) &&
                    DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedExpiry))
                {
                    attemptsCount = parsedCount;
                    expiryTime = parsedExpiry;
                }
            }

            if (attemptsCount >= 5) return; // Rate limit reached, skip automatically

            // Check 60-second cooldown
            var cooldownKey = $"resend:cooldown:{user.Id}";
            var cooldownString = await _cache.GetStringAsync(cooldownKey, ct);
            if (!string.IsNullOrEmpty(cooldownString)) return; // Cooldown active, skip

            // Set cooldown and update attempts in cache
            var cooldownOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) };
            await _cache.SetStringAsync(cooldownKey, "1", cooldownOptions, ct);

            var remainingTtl = expiryTime - DateTime.UtcNow;
            if (remainingTtl < TimeSpan.Zero) remainingTtl = TimeSpan.FromSeconds(1);

            var windowOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remainingTtl };
            var nextAttemptsString = $"{attemptsCount + 1}|{expiryTime:O}";
            await _cache.SetStringAsync(windowKey, nextAttemptsString, windowOptions, ct);

            // Simulate dispatching a verification email
            _logger.LogInformation("[Verification] Generated verification token and dispatched verification email for user: {Email}", user.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while triggering verification email for user. UserId: {UserId}", user.Id);
        }
    }
}
