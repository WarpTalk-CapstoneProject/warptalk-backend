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
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtGenerator;
    private readonly IDistributedCache _cache;
    private readonly IOptions<AuthSettings> _authSettingsOptions;
    private readonly ILogger<WarpTalk.AuthService.Application.Services.AuthService> _logger;
    private readonly WarpTalk.AuthService.Application.Services.AuthService _authService;

    public AuthServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userRepository = Substitute.For<IUserRepository>();
        _refreshTokenRepository = Substitute.For<IRefreshTokenRepository>();
        _passwordHasher = Substitute.For<IPasswordHasher>();
        _jwtGenerator = Substitute.For<IJwtTokenGenerator>();
        _cache = Substitute.For<IDistributedCache>();
        _logger = Substitute.For<ILogger<WarpTalk.AuthService.Application.Services.AuthService>>();

        _unitOfWork.UserRepository.Returns(_userRepository);
        _unitOfWork.RefreshTokenRepository.Returns(_refreshTokenRepository);

        var settings = new AuthSettings
        {
            DefaultRole = "user",
            MaxFailedAttempts = 5,
            LockoutDurationMinutes = 15
        };
        _authSettingsOptions = Options.Create(settings);

        _authService = new WarpTalk.AuthService.Application.Services.AuthService(
            _unitOfWork,
            _passwordHasher,
            _jwtGenerator,
            _cache,
            _authSettingsOptions,
            _logger
        );
    }

    private void MockCacheGet(string key, string? value)
    {
        var bytes = value == null ? null : Encoding.UTF8.GetBytes(value);
        _cache.GetAsync(key, Arg.Any<CancellationToken>()).Returns(Task.FromResult(bytes));
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
    public async Task RegisterAsync_ShouldCreateDefaultPersonalWorkspace_WhenRegistrationSucceeds()
    {
        // Arrange
        var request = new RegisterRequest("newuser@warptalk.vn", "password123", "New User");
        _userRepository.ExistsByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        
        // Mock role repository
        var role = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        _unitOfWork.RoleRepository.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Role, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(role);

        // Mock workspace repository for Slug collision checks
        _unitOfWork.WorkspaceRepository.AnyAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Workspace, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _passwordHasher.Hash(Arg.Any<string>()).Returns("hashed_pwd");

        Workspace? capturedWorkspace = null;
        _unitOfWork.WorkspaceRepository.AddAsync(Arg.Do<Workspace>(w => capturedWorkspace = w), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        var result = await _authService.RegisterAsync(request);

        // Assert
        Assert.True(result.IsSuccess);

        Assert.NotNull(capturedWorkspace);
        Assert.Equal("New User's Workspace", capturedWorkspace.Name);
        Assert.Equal("personal", capturedWorkspace.Type);
        Assert.Equal(AuthConstants.PlanTierFree, capturedWorkspace.PlanTier);

        // Verify WorkspaceMember Owner is created
        await _unitOfWork.WorkspaceMemberRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceMember>(m => m.Status == "Active"),
            Arg.Any<CancellationToken>()
        );

        // Verify UnitOfWork saved changes three times (once for user, once for workspace, once for token)
        await _unitOfWork.Received(3).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
