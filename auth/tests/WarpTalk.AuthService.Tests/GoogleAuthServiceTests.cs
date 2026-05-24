using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

public class GoogleAuthServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IJwtTokenGenerator _jwtGenerator;
    private readonly IGoogleTokenVerifier _googleTokenVerifier;
    private readonly IDistributedCache _cache;
    private readonly IOptions<AuthSettings> _authSettingsOptions;
    private readonly WarpTalk.AuthService.Application.Services.GoogleAuthService _googleAuthService;

    public GoogleAuthServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userRepository = Substitute.For<IUserRepository>();
        _refreshTokenRepository = Substitute.For<IRefreshTokenRepository>();
        _jwtGenerator = Substitute.For<IJwtTokenGenerator>();
        _googleTokenVerifier = Substitute.For<IGoogleTokenVerifier>();
        _cache = Substitute.For<IDistributedCache>();

        _unitOfWork.UserRepository.Returns(_userRepository);
        _unitOfWork.RefreshTokenRepository.Returns(_refreshTokenRepository);

        var settings = new AuthSettings
        {
            DefaultRole = "User"
        };
        _authSettingsOptions = Options.Create(settings);

        _googleAuthService = new WarpTalk.AuthService.Application.Services.GoogleAuthService(
            _unitOfWork,
            _jwtGenerator,
            _googleTokenVerifier,
            _cache,
            _authSettingsOptions,
            Substitute.For<ILogger<WarpTalk.AuthService.Application.Services.GoogleAuthService>>()
        );
    }

    [Fact]
    public async Task GoogleLoginAsync_ShouldBlockDisabledAccount_WhenUserIsDeactivated()
    {
        // Arrange
        var request = new GoogleLoginRequest("google_id_token", "127.0.0.1", "Chrome");
        var payload = new GoogleAuthPayload("sub123", "disabled@warptalk.vn", "Disabled User", "pic.jpg", true);

        var user = new User
        {
            Email = payload.Email,
            IsActive = false,
            IsLocked = false,
            EmailVerified = true
        };

        _googleTokenVerifier.VerifyGoogleTokenAsync(request.IdToken, Arg.Any<CancellationToken>()).Returns(payload);
        _userRepository.GetByEmailWithRolesAsync(payload.Email, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _googleAuthService.GoogleLoginAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountInactive, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountInactive, result.Error);
    }

    [Fact]
    public async Task GoogleLoginAsync_ShouldBlockLockedAccount_WhenUserIsLocked()
    {
        // Arrange
        var request = new GoogleLoginRequest("google_id_token", "127.0.0.1", "Chrome");
        var payload = new GoogleAuthPayload("sub123", "locked@warptalk.vn", "Locked User", "pic.jpg", true);

        var user = new User
        {
            Email = payload.Email,
            IsActive = true,
            IsLocked = true,
            EmailVerified = true
        };

        _googleTokenVerifier.VerifyGoogleTokenAsync(request.IdToken, Arg.Any<CancellationToken>()).Returns(payload);
        _userRepository.GetByEmailWithRolesAsync(payload.Email, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _googleAuthService.GoogleLoginAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountLocked, result.ErrorCode);
    }

    [Fact]
    public async Task GoogleLoginAsync_ShouldBlockUnverifiedAccount_WhenEmailVerifiedIsFalse()
    {
        // Arrange
        var request = new GoogleLoginRequest("google_id_token", "127.0.0.1", "Chrome");
        var payload = new GoogleAuthPayload("sub123", "unverified@warptalk.vn", "Unverified User", "pic.jpg", false);

        var user = new User
        {
            Email = payload.Email,
            IsActive = true,
            IsLocked = false,
            EmailVerified = false
        };

        _googleTokenVerifier.VerifyGoogleTokenAsync(request.IdToken, Arg.Any<CancellationToken>()).Returns(payload);
        _userRepository.GetByEmailWithRolesAsync(payload.Email, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _googleAuthService.GoogleLoginAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountPending, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountPending, result.Error);
    }

    [Fact]
    public async Task GoogleLoginAsync_ShouldFail_WhenGoogleTokenIsInvalid()
    {
        // Arrange
        var request = new GoogleLoginRequest("invalid_id_token", "127.0.0.1", "Chrome");
        _googleTokenVerifier.VerifyGoogleTokenAsync(request.IdToken, Arg.Any<CancellationToken>()).Returns((GoogleAuthPayload?)null);

        // Act
        var result = await _googleAuthService.GoogleLoginAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidToken, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorGoogleTokenInvalid, result.Error);
    }

    [Fact]
    public async Task GoogleLoginAsync_ShouldFailAndResendEmail_WhenEmailMatchesExistingButUnverifiedLocalUser()
    {
        // Arrange
        var request = new GoogleLoginRequest("google_id_token", "127.0.0.1", "Chrome");
        var payload = new GoogleAuthPayload("sub123", "unverified_exist@warptalk.vn", "Unverified Exist", "pic.jpg", true);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = payload.Email,
            IsActive = true,
            IsLocked = false,
            EmailVerified = false,
            PasswordHash = "hashed_password",
            GoogleId = null
        };

        _googleTokenVerifier.VerifyGoogleTokenAsync(request.IdToken, Arg.Any<CancellationToken>()).Returns(payload);
        _userRepository.GetByEmailWithRolesAsync(payload.Email, Arg.Any<CancellationToken>()).Returns(user);
        _userRepository.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _googleAuthService.GoogleLoginAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.EmailNotVerified, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorEmailNotVerified, result.Error);

        // Verify that distributed cache is accessed to handle verification email resend tracking (cooldown/window checks)
        await _cache.Received(1).GetStringAsync(Arg.Is<string>(k => k == $"resend:window:{user.Id}"), Arg.Any<CancellationToken>());
        await _cache.Received(1).GetStringAsync(Arg.Is<string>(k => k == $"resend:cooldown:{user.Id}"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkGoogleAsync_ShouldSucceed_WhenValidTokenAndMatchingEmail()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new LinkGoogleRequest("google_id_token");
        var payload = new GoogleAuthPayload("sub123", "alex@warptalk.vn", "Alex", "pic.jpg", true);
        var user = new User { Id = userId, Email = "alex@warptalk.vn", GoogleId = null };

        _googleTokenVerifier.VerifyGoogleTokenAsync(request.IdToken, Arg.Any<CancellationToken>()).Returns(payload);
        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _googleAuthService.LinkGoogleAsync(userId, request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("sub123", user.GoogleId);
        _userRepository.Received(1).Update(user);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkGoogleAsync_ShouldFail_WhenEmailMismatches()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new LinkGoogleRequest("google_id_token");
        var payload = new GoogleAuthPayload("sub123", "different@warptalk.vn", "Alex", "pic.jpg", true);
        var user = new User { Id = userId, Email = "alex@warptalk.vn", GoogleId = null };

        _googleTokenVerifier.VerifyGoogleTokenAsync(request.IdToken, Arg.Any<CancellationToken>()).Returns(payload);
        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _googleAuthService.LinkGoogleAsync(userId, request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
    }

    [Fact]
    public async Task UnlinkGoogleAsync_ShouldSucceed_WhenUserHasPassword()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "alex@warptalk.vn", GoogleId = "sub123", PasswordHash = "some_hash" };
        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _googleAuthService.UnlinkGoogleAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Null(user.GoogleId);
        _userRepository.Received(1).Update(user);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlinkGoogleAsync_ShouldFail_WhenUserHasNoPassword()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "alex@warptalk.vn", GoogleId = "sub123", PasswordHash = null };
        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _googleAuthService.UnlinkGoogleAsync(userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.MinAuthMethodRequired, result.ErrorCode);
        Assert.Equal("sub123", user.GoogleId); // Ensure it is NOT nullified
    }
}
