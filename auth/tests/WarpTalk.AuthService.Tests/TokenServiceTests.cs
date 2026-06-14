using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
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

public class TokenServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IJwtTokenGenerator _jwtGenerator;
    private readonly IOptions<AuthSettings> _authSettingsOptions;
    private readonly WarpTalk.AuthService.Application.Services.TokenService _tokenService;

    public TokenServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userRepository = Substitute.For<IUserRepository>();
        _refreshTokenRepository = Substitute.For<IRefreshTokenRepository>();
        _jwtGenerator = Substitute.For<IJwtTokenGenerator>();

        _unitOfWork.UserRepository.Returns(_userRepository);
        _unitOfWork.RefreshTokenRepository.Returns(_refreshTokenRepository);

        var settings = new AuthSettings
        {
            DefaultRole = "User"
        };
        _authSettingsOptions = Options.Create(settings);

        _tokenService = new WarpTalk.AuthService.Application.Services.TokenService(
            _unitOfWork,
            _jwtGenerator,
            _authSettingsOptions,
            Substitute.For<ILogger<WarpTalk.AuthService.Application.Services.TokenService>>()
        );
    }

    [Fact]
    public async Task RefreshTokenAsync_ShouldBlockDisabledAccount_WhenUserIsDeactivated()
    {
        // Arrange
        var tokenString = "valid_refresh_token";
        var tokenHash = TokenHasher.Hash(tokenString);
        var storedToken = new RefreshToken
        {
            TokenHash = tokenHash,
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RevokedAt = null
        };

        var user = new User
        {
            Id = storedToken.UserId,
            Email = "disabled@warptalk.vn",
            IsActive = false,
            IsLocked = false,
            EmailVerified = true
        };

        _refreshTokenRepository.GetByTokenHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(storedToken);
        _userRepository.GetByIdWithRolesAsync(storedToken.UserId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new RefreshTokenRequest(tokenString, "127.0.0.1", "Chrome");

        // Act
        var result = await _tokenService.RefreshTokenAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountInactive, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountInactive, result.Error);
    }

    [Fact]
    public async Task RefreshTokenAsync_ShouldBlockLockedAccount_WhenUserIsLocked()
    {
        // Arrange
        var tokenString = "valid_refresh_token";
        var tokenHash = TokenHasher.Hash(tokenString);
        var storedToken = new RefreshToken
        {
            TokenHash = tokenHash,
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RevokedAt = null
        };

        var user = new User
        {
            Id = storedToken.UserId,
            Email = "locked@warptalk.vn",
            IsActive = true,
            IsLocked = true,
            EmailVerified = true
        };

        _refreshTokenRepository.GetByTokenHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(storedToken);
        _userRepository.GetByIdWithRolesAsync(storedToken.UserId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new RefreshTokenRequest(tokenString, "127.0.0.1", "Chrome");

        // Act
        var result = await _tokenService.RefreshTokenAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountLocked, result.ErrorCode);
    }

    [Fact]
    public async Task RefreshTokenAsync_ShouldBlockUnverifiedAccount_WhenEmailVerifiedIsFalse()
    {
        // Arrange
        var tokenString = "valid_refresh_token";
        var tokenHash = TokenHasher.Hash(tokenString);
        var storedToken = new RefreshToken
        {
            TokenHash = tokenHash,
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RevokedAt = null
        };

        var user = new User
        {
            Id = storedToken.UserId,
            Email = "unverified@warptalk.vn",
            IsActive = true,
            IsLocked = false,
            EmailVerified = false
        };

        _refreshTokenRepository.GetByTokenHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(storedToken);
        _userRepository.GetByIdWithRolesAsync(storedToken.UserId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new RefreshTokenRequest(tokenString, "127.0.0.1", "Chrome");

        // Act
        var result = await _tokenService.RefreshTokenAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountPending, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountPending, result.Error);
    }
}
