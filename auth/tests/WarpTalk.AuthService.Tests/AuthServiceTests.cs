using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

public class AuthServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IUserSettingRepository _userSettingRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtGenerator;
    private readonly IDistributedCache _cache;
    private readonly IOptions<AuthSettings> _authSettingsOptions;
    private readonly ILogger<WarpTalk.AuthService.Application.Services.AuthService> _logger;
    private readonly IWorkspaceInvitationClient _workspaceInvitationClient;
    private readonly IAuthEmailSender _authEmailSender;
    private readonly WarpTalk.AuthService.Application.Services.AuthService _authService;

    public AuthServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userRepository = Substitute.For<IUserRepository>();
        _userSettingRepository = Substitute.For<IUserSettingRepository>();
        _refreshTokenRepository = Substitute.For<IRefreshTokenRepository>();
        _passwordHasher = Substitute.For<IPasswordHasher>();
        _jwtGenerator = Substitute.For<IJwtTokenGenerator>();
        _cache = Substitute.For<IDistributedCache>();
        _logger = Substitute.For<ILogger<WarpTalk.AuthService.Application.Services.AuthService>>();
        _workspaceInvitationClient = Substitute.For<IWorkspaceInvitationClient>();
        _authEmailSender = Substitute.For<IAuthEmailSender>();

        _unitOfWork.UserRepository.Returns(_userRepository);
        _unitOfWork.RefreshTokenRepository.Returns(_refreshTokenRepository);
        _unitOfWork.UserSettingRepository.Returns(_userSettingRepository);

        var settings = new AuthSettings
        {
            DefaultRole = "user",
            MaxFailedAttempts = 5,
            LockoutDurationMinutes = 15,
            // Mirrors the production default. Any test that wants a self-registration treated as
            // verified must now say so, because that is a security-relevant deviation and not
            // something a fixture should hand out silently.
            AutoVerifySelfRegistration = false
        };
        _authSettingsOptions = Options.Create(settings);

        _authService = new WarpTalk.AuthService.Application.Services.AuthService(
            _unitOfWork,
            _passwordHasher,
            _jwtGenerator,
            _cache,
            _authSettingsOptions,
            _logger,
            _workspaceInvitationClient,
            _authEmailSender
        );
    }

    private void MockCacheGet(string key, string? value)
    {
        var bytes = value == null ? null : Encoding.UTF8.GetBytes(value);
        _cache.GetAsync(key, Arg.Any<CancellationToken>()).Returns(Task.FromResult(bytes));
    }

    [Fact]
    public async Task RegisterAsync_ShouldCreateDefaultUserSettings_ForNewSelfRegisteredUser()
    {
        // Arrange — RegisterInvitedAsync already creates a default user_settings row for
        // invited users; self-registration was silently skipping it (regression guard).
        _userRepository.ExistsByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _passwordHasher.Hash(Arg.Any<string>()).Returns("hashed_password");

        var request = new RegisterRequest("new-user@warptalk.vn", "Password123!", "New User");

        // Act
        var result = await _authService.RegisterAsync(request);

        // Assert
        Assert.True(result.IsSuccess);
        // BR-02: register no longer returns a session, so the created user's id is read from
        // what was persisted rather than from a response that deliberately no longer carries it.
        await _userRepository.Received(1).AddAsync(
            Arg.Any<User>(),
            Arg.Any<CancellationToken>());
        await _userSettingRepository.Received(1).AddAsync(
            Arg.Any<UserSetting>(),
            Arg.Any<CancellationToken>()
        );
    }

    /// <summary>
    /// The sign-up wizard's third step has to land somewhere, and this is the only place it can.
    ///
    /// Registration returns no session (BR-02), so there is no authenticated moment between
    /// "account created" and "first meeting" in which the client could save these. Before the
    /// wizard existed, every new account's first meeting ran on the platform default speak/listen
    /// pair — TranslationRoomService reads exactly this row when somebody joins — and the only way
    /// to discover that was to notice it mid-meeting.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_ShouldPersistTheLanguagesTheWizardAskedFor()
    {
        _userRepository.ExistsByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _passwordHasher.Hash(Arg.Any<string>()).Returns("hashed_password");

        UserSetting? persisted = null;
        await _userSettingRepository.AddAsync(
            Arg.Do<UserSetting>(setting => persisted = setting),
            Arg.Any<CancellationToken>());

        var result = await _authService.RegisterAsync(new RegisterRequest(
            "vi-user@warptalk.vn", "Password123!", "Vi User", "vi-VN", "en-US"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(persisted);
        Assert.Equal("vi-VN", persisted!.DefaultSpeakLanguage);
        Assert.Equal("en-US", persisted.DefaultListenLanguage);
    }

    [Fact]
    public async Task RegisterAsync_ShouldFallBackToPlatformDefaults_WhenNoLanguagesWereAsked()
    {
        // The Google path and any older client send no languages at all. A missing answer is not
        // an invalid one — it must produce a usable row, not an empty column.
        _userRepository.ExistsByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _passwordHasher.Hash(Arg.Any<string>()).Returns("hashed_password");

        UserSetting? persisted = null;
        await _userSettingRepository.AddAsync(
            Arg.Do<UserSetting>(setting => persisted = setting),
            Arg.Any<CancellationToken>());

        await _authService.RegisterAsync(
            new RegisterRequest("plain@warptalk.vn", "Password123!", "Plain User"));

        Assert.NotNull(persisted);
        Assert.Equal(UserConstants.DefaultSpeakLanguage, persisted!.DefaultSpeakLanguage);
        Assert.Equal(UserConstants.DefaultListenLanguage, persisted.DefaultListenLanguage);
    }

    /// <summary>
    /// The spec-137 anti-takeover guard. <c>AutoVerifySelfRegistration</c> defaulted to true and
    /// was set nowhere, so every self-registered address was treated as proven — which meant
    /// someone could register an address they did not control and be trusted as its owner.
    /// </summary>
    [Fact]
    public void AutoVerifySelfRegistration_ShouldDefaultToFalse()
    {
        Assert.False(new AuthSettings().AutoVerifySelfRegistration);
    }

    [Fact]
    public async Task RegisterAsync_ShouldNotMarkEmailVerified_WhenAutoVerifyIsOff()
    {
        _authSettingsOptions.Value.AutoVerifySelfRegistration = false;
        _userRepository.ExistsByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _passwordHasher.Hash(Arg.Any<string>()).Returns("hashed_password");

        var result = await _authService.RegisterAsync(
            new RegisterRequest("unproven@warptalk.vn", "Password123!", "Unproven"));

        Assert.True(result.IsSuccess);
        // BR-02 — the stronger claim, and the one this bug was about: an unverified registration
        // hands back NO session at all. Asserting `EmailVerified == false` on a response that also
        // contained working tokens is exactly the state that let register bypass the login gate.
        Assert.True(result.Value!.EmailVerificationRequired);
        Assert.Null(result.Value.Auth);

        // And the address must actually be challenged rather than just left unflagged.
        await _userRepository.Received(1).AddAsync(
            Arg.Is<User>(u =>
                !u.EmailVerified
                && u.EmailVerificationTokenHash != null
                && u.EmailVerificationTokenExpiresAt != null),
            Arg.Any<CancellationToken>());
        await _authEmailSender.Received(1).SendVerificationEmailAsync(
            Arg.Any<User>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The opt-in still works, but a caller has to ask for it explicitly.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_ShouldMarkEmailVerified_OnlyWhenAutoVerifyIsExplicitlyEnabled()
    {
        _authSettingsOptions.Value.AutoVerifySelfRegistration = true;
        _userRepository.ExistsByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _passwordHasher.Hash(Arg.Any<string>()).Returns("hashed_password");

        var result = await _authService.RegisterAsync(
            new RegisterRequest("auto-verified@warptalk.vn", "Password123!", "Auto Verified"));

        Assert.True(result.IsSuccess);
        // AutoVerifySelfRegistration on: the address is already proven, so a session is correct
        // and BR-02 must not withhold it.
        Assert.False(result.Value!.EmailVerificationRequired);
        Assert.NotNull(result.Value.Auth);
        Assert.True(result.Value.Auth!.User.EmailVerified);
        await _authEmailSender.DidNotReceive().SendVerificationEmailAsync(
            Arg.Any<User>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_ShouldReturnSuccess_WhenVerificationEmailDeliveryFailsAfterPersistence()
    {
        _authSettingsOptions.Value.AutoVerifySelfRegistration = false;
        _userRepository.ExistsByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _passwordHasher.Hash(Arg.Any<string>()).Returns("hashed_password");
        _authEmailSender
            .SendVerificationEmailAsync(
                Arg.Any<User>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("provider unavailable")));

        var result = await _authService.RegisterAsync(
            new RegisterRequest("delivery-failure@warptalk.vn", "Password123!", "Delivery Failure"));

        Assert.True(result.IsSuccess);
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_ShouldBlockDisabledAccount_WhenIsActiveIsFalse()
    {
        // Arrange
        var user = new User
        {
            Email = "disabled@warptalk.vn",
            PasswordHash = "hashed_password",
            IsActive = false,
            IsLocked = false,
            EmailVerified = true
        };

        _userRepository.GetByEmailWithRolesAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);

        var request = new LoginRequest(user.Email, "password", "127.0.0.1", "Chrome");

        // Act
        var result = await _authService.LoginAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountInactive, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountInactive, result.Error);
    }

    [Fact]
    public async Task LoginAsync_ShouldBlockDeletedAccount_WhenDeletedAtIsNotNull()
    {
        // Arrange
        var user = new User
        {
            Email = "deleted@warptalk.vn",
            PasswordHash = "hashed_password",
            IsActive = true,
            IsLocked = false,
            EmailVerified = true,
            DeletedAt = DateTime.UtcNow
        };

        _userRepository.GetByEmailWithRolesAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);

        var request = new LoginRequest(user.Email, "password", "127.0.0.1", "Chrome");

        // Act
        var result = await _authService.LoginAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidCredentials, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorInvalidCredentials, result.Error);
    }

    [Fact]
    public async Task LoginAsync_ShouldBlockLockedAccount_WhenIsLockedIsTrue()
    {
        // Arrange
        var user = new User
        {
            Email = "locked@warptalk.vn",
            PasswordHash = "hashed_password",
            IsActive = true,
            IsLocked = true,
            EmailVerified = true
        };

        _userRepository.GetByEmailWithRolesAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);

        var request = new LoginRequest(user.Email, "password", "127.0.0.1", "Chrome");

        // Act
        var result = await _authService.LoginAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountLocked, result.ErrorCode);
    }

    [Fact]
    public async Task LoginAsync_ShouldBlockUnverifiedAccount_WhenEmailVerifiedIsFalse()
    {
        // Arrange
        var user = new User
        {
            Email = "unverified@warptalk.vn",
            PasswordHash = "hashed_password",
            IsActive = true,
            IsLocked = false,
            EmailVerified = false
        };

        _userRepository.GetByEmailWithRolesAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);

        var request = new LoginRequest(user.Email, "password", "127.0.0.1", "Chrome");

        // Act
        var result = await _authService.LoginAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountPending, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountPending, result.Error);
    }

    [Fact]
    public async Task ResendVerificationAsync_ShouldEnforceCooldown_WhenRequestIsTooFrequent()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "test@warptalk.vn",
            EmailVerified = false,
            IsActive = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Mock 60-second cooldown is active (using new format attemptsCount|expiryIsoString)
        MockCacheGet($"resend:window:{userId}", "1|2026-05-22T13:30:00Z");
        MockCacheGet($"resend:cooldown:{userId}", "1");

        // Act
        var result = await _authService.ResendVerificationAsync(userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.CooldownActive, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorCooldownActive, result.Error);
    }

    [Fact]
    public async Task ResendVerificationAsync_ShouldEnforceRateLimit_WhenMaxAttemptsExceeded()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "test@warptalk.vn",
            EmailVerified = false,
            IsActive = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Mock 5 attempts reached in 15 mins (using new format attemptsCount|expiryIsoString)
        MockCacheGet($"resend:window:{userId}", "5|2026-05-22T13:30:00Z");
        MockCacheGet($"resend:cooldown:{userId}", null); // no 60s cooldown currently active

        // Act
        var result = await _authService.ResendVerificationAsync(userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.RateLimitExceeded, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorRateLimitExceeded, result.Error);
    }

    [Fact]
    public async Task ResendVerificationAsync_ShouldSucceed_WhenAccountIsEligible()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "test@warptalk.vn",
            EmailVerified = false,
            IsActive = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Mock eligible state (no cooldown, attempts < 5, existing window at 2 attempts)
        var expiry = DateTime.UtcNow.AddMinutes(10);
        MockCacheGet($"resend:window:{userId}", $"2|{expiry:O}");
        MockCacheGet($"resend:cooldown:{userId}", null);

        // Act
        var result = await _authService.ResendVerificationAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);

        await _authEmailSender.Received(1).SendVerificationEmailAsync(
            user,
            Arg.Is<string>(token => !string.IsNullOrWhiteSpace(token)),
            Arg.Any<CancellationToken>()
        );
        Assert.NotNull(user.EmailVerificationTokenHash);
        Assert.NotNull(user.EmailVerificationTokenExpiresAt);
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());

        // Confirm cache is updated
        await _cache.Received(1).SetAsync(
            $"resend:cooldown:{userId}",
            Arg.Any<byte[]>(),
            Arg.Any<DistributedCacheEntryOptions>(),
            Arg.Any<CancellationToken>()
        );
        await _cache.Received(1).SetAsync(
            $"resend:window:{userId}",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == $"3|{expiry:O}"),
            Arg.Any<DistributedCacheEntryOptions>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ResendVerificationAsync_ShouldCreateNewWindow_WhenCacheIsEmpty()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "test@warptalk.vn",
            EmailVerified = false,
            IsActive = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // First attempt (no window cache, no cooldown)
        MockCacheGet($"resend:window:{userId}", null);
        MockCacheGet($"resend:cooldown:{userId}", null);

        // Act
        var result = await _authService.ResendVerificationAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);

        // Confirm window is created with attempt = 1
        await _cache.Received(1).SetAsync(
            $"resend:window:{userId}",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b).StartsWith("1|")),
            Arg.Any<DistributedCacheEntryOptions>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ResendVerificationAsync_ShouldFallbackToLegacyFormat_WhenCacheContainsSingleNumber()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "test@warptalk.vn",
            EmailVerified = false,
            IsActive = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Mock legacy style attemptsCount without pipe
        MockCacheGet($"resend:window:{userId}", "3");
        MockCacheGet($"resend:cooldown:{userId}", null);

        // Act
        var result = await _authService.ResendVerificationAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);

        // Confirm it parsed "3" as 3, and saved next attempt count as "4" (creating a new window from now)
        await _cache.Received(1).SetAsync(
            $"resend:window:{userId}",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b).StartsWith("4|")),
            Arg.Any<DistributedCacheEntryOptions>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ResendVerificationAsync_ShouldResetWindow_WhenCacheContainsMalformedGarbage()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "test@warptalk.vn",
            EmailVerified = false,
            IsActive = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Mock garbage values in cache
        MockCacheGet($"resend:window:{userId}", "corrupted|garbage-date-format");
        MockCacheGet($"resend:cooldown:{userId}", null);

        // Act
        var result = await _authService.ResendVerificationAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);

        // Confirm it gracefully ignored garbage, falling back to 0 attempts, and saving attempt as "1" in a new window
        await _cache.Received(1).SetAsync(
            $"resend:window:{userId}",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b).StartsWith("1|")),
            Arg.Any<DistributedCacheEntryOptions>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task VerifyEmailAsync_ShouldConsumeValidToken()
    {
        var token = "verification-token";
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "verify@warptalk.vn",
            EmailVerified = false,
            IsActive = true,
            EmailVerificationTokenHash = TokenHashing.Hash(token),
            EmailVerificationTokenExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };
        _userRepository.GetByEmailVerificationTokenHashAsync(
                user.EmailVerificationTokenHash,
                Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _authService.VerifyEmailAsync(new VerifyEmailRequest(token));

        Assert.True(result.IsSuccess);
        Assert.True(user.EmailVerified);
        Assert.NotNull(user.EmailVerifiedAt);
        Assert.Null(user.EmailVerificationTokenHash);
        Assert.Null(user.EmailVerificationTokenExpiresAt);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgotPasswordAsync_ShouldNotRevealUnknownEmail()
    {
        _userRepository.GetByEmailWithRolesAsync(
                "missing@warptalk.vn",
                Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _authService.ForgotPasswordAsync(
            new ForgotPasswordRequest("missing@warptalk.vn"));

        Assert.True(result.IsSuccess);
        await _authEmailSender.DidNotReceiveWithAnyArgs()
            .SendPasswordResetEmailAsync(default!, default!, default);
    }

    [Fact]
    public async Task ForgotPasswordAsync_ShouldPersistHashedTokenAndSendEmail()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "reset@warptalk.vn",
            EmailVerified = true,
            IsActive = true
        };
        _userRepository.GetByEmailWithRolesAsync(
                user.Email,
                Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _authService.ForgotPasswordAsync(
            new ForgotPasswordRequest(user.Email));

        Assert.True(result.IsSuccess);
        Assert.NotNull(user.PasswordResetTokenHash);
        Assert.NotNull(user.PasswordResetTokenExpiresAt);
        await _authEmailSender.Received(1).SendPasswordResetEmailAsync(
            user,
            Arg.Is<string>(token =>
                TokenHashing.Hash(token) == user.PasswordResetTokenHash),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetPasswordAsync_ShouldConsumeValidTokenAndRevokeRefreshTokens()
    {
        var token = "password-reset-token";
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "reset@warptalk.vn",
            EmailVerified = true,
            IsActive = true,
            PasswordResetTokenHash = TokenHashing.Hash(token),
            PasswordResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };
        _userRepository.GetByPasswordResetTokenHashAsync(
                user.PasswordResetTokenHash,
                Arg.Any<CancellationToken>())
            .Returns(user);
        _passwordHasher.Hash("NewPassword123!").Returns("new-password-hash");

        var result = await _authService.ResetPasswordAsync(
            new ResetPasswordRequest(token, "NewPassword123!"));

        Assert.True(result.IsSuccess);
        Assert.Equal("new-password-hash", user.PasswordHash);
        Assert.Null(user.PasswordResetTokenHash);
        Assert.Null(user.PasswordResetTokenExpiresAt);
        await _refreshTokenRepository.Received(1)
            .RevokeAllForUserAsync(user.Id, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

}
