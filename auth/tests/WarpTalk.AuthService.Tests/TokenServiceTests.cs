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

    #region LogoutAsync Tests

    /// <summary>
    /// Logout revoked only the presented leaf token, leaving the rest of the rotation family
    /// intact — so "log out" ended the session on paper only. RefreshTokenAsync already revokes
    /// by family when it merely *suspects* theft; a deliberate logout should be at least as
    /// thorough.
    /// </summary>
    [Fact]
    public async Task LogoutAsync_ShouldRevokeTheWholeRotationFamily()
    {
        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        const string presented = "presented-refresh-token";

        _refreshTokenRepository
            .GetByTokenHashAsync(TokenHasher.Hash(presented), Arg.Any<CancellationToken>())
            .Returns(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FamilyId = familyId,
                TokenHash = TokenHasher.Hash(presented),
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            });

        var result = await _tokenService.LogoutAsync(userId, presented);

        Assert.True(result.IsSuccess);
        await _refreshTokenRepository.Received(1).RevokeFamilyAsync(familyId, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The reported concern: a caller presenting somebody else's refresh token must not be able
    /// to revoke it. Confirmed the ownership check does hold — this pins it, because a separate
    /// client-side change is being written against this guarantee.
    /// </summary>
    [Fact]
    public async Task LogoutAsync_ShouldNotRevokeATokenTheCallerDoesNotOwn()
    {
        var caller = Guid.NewGuid();
        var victim = Guid.NewGuid();
        const string victimsToken = "victims-refresh-token";

        _refreshTokenRepository
            .GetByTokenHashAsync(TokenHasher.Hash(victimsToken), Arg.Any<CancellationToken>())
            .Returns(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = victim,
                FamilyId = Guid.NewGuid(),
                TokenHash = TokenHasher.Hash(victimsToken),
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            });

        var result = await _tokenService.LogoutAsync(caller, victimsToken);

        // Success, deliberately: logout is idempotent and must not become an oracle that tells
        // a caller whether a token exists. What matters is that nothing was revoked.
        Assert.True(result.IsSuccess);
        await _refreshTokenRepository.DidNotReceive().RevokeFamilyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogoutAsync_ShouldSucceedAndRevokeNothing_WhenTheTokenIsUnknown()
    {
        _refreshTokenRepository
            .GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((RefreshToken?)null);

        var result = await _tokenService.LogoutAsync(Guid.NewGuid(), "never-issued");

        Assert.True(result.IsSuccess);
        await _refreshTokenRepository.DidNotReceive().RevokeFamilyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion
}
