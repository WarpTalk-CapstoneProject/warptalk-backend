using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
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
    private readonly IDistributedCache _cache;
    private readonly AuthSettings _authSettings;
    private readonly ILogger<AuthService> _logger;
    private readonly TimeSpan _lockoutDuration;
    private readonly IWorkspaceInvitationClient _workspaceInvitationClient;
    private readonly IAuthEmailSender _authEmailSender;

    public AuthService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtGenerator,
        IDistributedCache cache,
        IOptions<AuthSettings> authSettings,
        ILogger<AuthService> logger,
        IWorkspaceInvitationClient workspaceInvitationClient,
        IAuthEmailSender authEmailSender)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _jwtGenerator = jwtGenerator;
        _cache = cache;
        _authSettings = authSettings.Value;
        _logger = logger;
        _lockoutDuration = TimeSpan.FromMinutes(_authSettings.LockoutDurationMinutes);
        _userRepository = _unitOfWork.UserRepository;
        _refreshTokenRepository = _unitOfWork.RefreshTokenRepository;
        _workspaceInvitationClient = workspaceInvitationClient;
        _authEmailSender = authEmailSender;
    }

    public async Task<Result<RegisterResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        try
        {
            if (await _userRepository.ExistsByEmailAsync(request.Email.ToLowerInvariant().Trim(), ct))
                return Result.Failure<RegisterResponse>(AuthConstants.ErrorEmailExists, ErrorCodes.EmailExists);

            var passwordHash = _passwordHasher.Hash(request.Password);
            var user = UserMapper.ToUser(request, passwordHash);
            string? verificationToken = null;
            if (_authSettings.AutoVerifySelfRegistration)
            {
                user.EmailVerified = true;
                user.EmailVerifiedAt = DateTime.UtcNow;
                user.EmailVerificationTokenHash = null;
                user.EmailVerificationTokenExpiresAt = null;
            }
            else
            {
                verificationToken = TokenHashing.GenerateToken();
                user.EmailVerificationTokenHash = TokenHashing.Hash(verificationToken);
                user.EmailVerificationTokenExpiresAt = DateTime.UtcNow.AddMinutes(
                    _authSettings.VerificationTokenLifetimeMinutes);
            }

            await _userRepository.AddAsync(user, ct);

            // Every user needs a user_settings row — RegisterInvitedAsync already does
            // this; self-registration was silently skipping it.
            //
            // The languages the wizard asked for land here, at account creation, because this is
            // the last authenticated-server moment before the account goes dark: registration
            // returns no session (BR-02), so the client cannot save them afterwards. Absent, they
            // fall back to the platform defaults.
            var settings = UserSettingsMapper.CreateDefaultUserSettings(
                user.Id, request.DefaultSpeakLanguage, request.DefaultListenLanguage);
            await _unitOfWork.UserSettingRepository.AddAsync(settings, ct);

            await _unitOfWork.SaveChangesAsync(ct);
            if (verificationToken is not null)
            {
                try
                {
                    await _authEmailSender.SendVerificationEmailAsync(user, verificationToken, ct);
                }
                catch (Exception ex)
                {
                    // The user and verification token are already committed. Returning a
                    // registration failure here would invite duplicate retries while the
                    // account actually exists. The user can request another verification
                    // message through the dedicated resend endpoint.
                    _logger.LogError(
                        ex,
                        "Registration persisted but verification email delivery failed. UserId: {UserId}",
                        user.Id);
                }
            }

            // BR-02 — no session until the address is proven.
            //
            // `verificationToken is not null` is the same condition that decided whether to send a
            // verification email, so this cannot drift from it: exactly when we asked the user to
            // prove the address, we decline to sign them in. When AutoVerifySelfRegistration is on
            // the account is already verified and a session is correct.
            //
            // Issuing tokens here made registration a way around the login gate that
            // UserStatusHelper puts in front of an unverified account.
            if (verificationToken is not null)
            {
                return Result.Success(new RegisterResponse(EmailVerificationRequired: true, Auth: null));
            }

            var response = await AuthResponseHelper.CreateAuthResponseAsync(user, null, null, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct);
            return Result.Success(new RegisterResponse(EmailVerificationRequired: false, Auth: response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during registration. Email: {Email}", request.Email);
            return Result.Failure<RegisterResponse>("An unexpected error occurred during registration.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        try
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

            if (user.PasswordHash is null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= _authSettings.MaxFailedAttempts)
                {
                    user.IsLocked = true;
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

            await _unitOfWork.SaveChangesAsync(ct);

            var response = await AuthResponseHelper.CreateAuthResponseAsync(user, request.IpAddress, request.DeviceInfo, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during login. Email: {Email}", request.Email);
            return Result.Failure<AuthResponse>("An unexpected error occurred during login.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ResendVerificationAsync(Guid userId, CancellationToken ct = default)
    {
        try
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

            int attemptsCount = 0;
            DateTime expiryTime = DateTime.UtcNow.AddMinutes(15);

            if (!string.IsNullOrEmpty(attemptsString))
            {
                var parts = attemptsString.Split('|');
                if (parts.Length == 2 && int.TryParse(parts[0], out var count) && DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedExpiry))
                {
                    attemptsCount = count;
                    expiryTime = parsedExpiry;
                }
                else if (parts.Length == 1 && int.TryParse(parts[0], out var countOnly))
                {
                    attemptsCount = countOnly;
                }
            }

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

            var remainingTtl = expiryTime - DateTime.UtcNow;
            if (remainingTtl < TimeSpan.Zero)
            {
                remainingTtl = TimeSpan.FromSeconds(1);
            }

            var windowOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remainingTtl };
            var nextAttemptsString = $"{attemptsCount + 1}|{expiryTime:O}";
            await _cache.SetStringAsync(windowKey, nextAttemptsString, windowOptions, ct);

            var verificationToken = TokenHashing.GenerateToken();
            user.EmailVerificationTokenHash = TokenHashing.Hash(verificationToken);
            user.EmailVerificationTokenExpiresAt = DateTime.UtcNow.AddMinutes(
                _authSettings.VerificationTokenLifetimeMinutes);
            _userRepository.Update(user);
            await _unitOfWork.SaveChangesAsync(ct);
            await _authEmailSender.SendVerificationEmailAsync(user, verificationToken, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while resending verification email. UserId: {UserId}", userId);
            return Result.Failure("An unexpected error occurred while resending verification email.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AuthResponse>> RegisterInvitedAsync(RegisterInvitedRequest request, CancellationToken ct = default)
    {
        try
        {
            // 1. Verify invitation token via gRPC
            var inviteResult = await _workspaceInvitationClient.VerifyInvitationTokenAsync(request.Token, ct);
            if (!inviteResult.IsValid)
            {
                return Result.Failure<AuthResponse>(inviteResult.ErrorMessage ?? "Invalid invitation token.", ErrorCodes.ValidationError);
            }

            var email = inviteResult.Email!.ToLowerInvariant().Trim();

            // 2. Check if user already exists
            if (await _userRepository.ExistsByEmailAsync(email, ct))
            {
                return Result.Failure<AuthResponse>(AuthConstants.ErrorEmailExists, ErrorCodes.EmailExists);
            }

            // 3. Begin Transaction
            await _unitOfWork.BeginTransactionAsync(ct);

            // 4. Create User
            var passwordHash = _passwordHasher.Hash(request.Password);
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                PasswordHash = passwordHash,
                FullName = request.FullName.Trim(),
                EmailVerified = true,
                EmailVerifiedAt = DateTime.UtcNow,
                IsActive = true,
                IsLocked = false,
                FailedLoginAttempts = 0,
                PreferredLanguage = "vi-VN",
                Timezone = "UTC",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _userRepository.AddAsync(user, ct);

            // Create default user settings, with whatever the sign-up wizard asked for. An
            // invited account goes through the same three steps as a self-registered one — the
            // invitation only removes the email step, not the question about languages.
            var settings = UserSettingsMapper.CreateDefaultUserSettings(
                user.Id, request.DefaultSpeakLanguage, request.DefaultListenLanguage);
            await _unitOfWork.UserSettingRepository.AddAsync(settings, ct);

            await _unitOfWork.SaveChangesAsync(ct);

            // 5. Accept invitation via gRPC
            var acceptResult = await _workspaceInvitationClient.AcceptInvitationAsync(request.Token, user.Id, email, ct);
            if (!acceptResult.Success)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure<AuthResponse>(acceptResult.ErrorMessage ?? "Failed to join workspace.", ErrorCodes.Forbidden);
            }

            // 6. Commit Transaction
            await _unitOfWork.CommitTransactionAsync(ct);

            // 7. Create Auth response
            var response = await AuthResponseHelper.CreateAuthResponseAsync(user, null, null, _jwtGenerator, _refreshTokenRepository, _unitOfWork, _authSettings.DefaultRole, ct);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during register-invited. Token: {Token}", request.Token);
            try
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
            }
            catch
            {
                // Ignore rollback errors
            }
            return Result.Failure<AuthResponse>("An unexpected error occurred during registration.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> VerifyEmailAsync(VerifyEmailRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return Result.Failure("Verification token is required.", ErrorCodes.ValidationError);

        var user = await _userRepository.GetByEmailVerificationTokenHashAsync(
            TokenHashing.Hash(request.Token), ct);
        if (user is null || user.EmailVerificationTokenExpiresAt is null ||
            user.EmailVerificationTokenExpiresAt <= DateTime.UtcNow)
        {
            return Result.Failure("Verification token is invalid or expired.", ErrorCodes.ValidationError);
        }

        user.EmailVerified = true;
        user.EmailVerifiedAt = DateTime.UtcNow;
        user.EmailVerificationTokenHash = null;
        user.EmailVerificationTokenExpiresAt = null;
        _userRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _userRepository.GetByEmailWithRolesAsync(email, ct);
        if (user is null || user.DeletedAt is not null || !user.IsActive)
            return Result.Success();

        var token = TokenHashing.GenerateToken();
        user.PasswordResetTokenHash = TokenHashing.Hash(token);
        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(
            _authSettings.PasswordResetTokenLifetimeMinutes);
        _userRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);
        try
        {
            await _authEmailSender.SendPasswordResetEmailAsync(user, token, ct);
        }
        catch (Exception ex)
        {
            // Keep the response indistinguishable from an unknown address. The
            // token remains bounded and can be retried through a future request.
            _logger.LogError(ex, "Password reset email delivery failed for user {UserId}", user.Id);
        }
        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(
        ResetPasswordRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return Result.Failure("Reset token and new password are required.", ErrorCodes.ValidationError);
        }

        var user = await _userRepository.GetByPasswordResetTokenHashAsync(
            TokenHashing.Hash(request.Token), ct);
        if (user is null || user.PasswordResetTokenExpiresAt is null ||
            user.PasswordResetTokenExpiresAt <= DateTime.UtcNow)
        {
            return Result.Failure("Reset token is invalid or expired.", ErrorCodes.ValidationError);
        }

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.PasswordResetTokenHash = null;
        user.PasswordResetTokenExpiresAt = null;
        user.UpdatedAt = DateTime.UtcNow;
        _userRepository.Update(user);
        await _refreshTokenRepository.RevokeAllForUserAsync(user.Id, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
