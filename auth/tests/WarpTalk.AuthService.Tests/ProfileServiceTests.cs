using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

public class ProfileServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IOptions<AuthSettings> _authSettingsOptions;
    private readonly ProfileService _profileService;

    public ProfileServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userRepository = Substitute.For<IUserRepository>();
        _passwordHasher = Substitute.For<IPasswordHasher>();

        _unitOfWork.UserRepository.Returns(_userRepository);

        var settings = new AuthSettings
        {
            DefaultRole = "User",
            MaxFailedAttempts = 5,
            LockoutDurationMinutes = 15
        };
        _authSettingsOptions = Options.Create(settings);

        _profileService = new ProfileService(
            _unitOfWork,
            _passwordHasher,
            _authSettingsOptions,
            Substitute.For<ILogger<ProfileService>>()
        );
    }

    #region GetProfileAsync Tests

    [Fact]
    public async Task GetProfileAsync_ShouldSucceed_WhenAccountIsActive()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "active@warptalk.vn",
            FullName = "Active User",
            IsActive = true,
            IsLocked = false,
            EmailVerified = true
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _profileService.GetProfileAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Active User", result.Value.FullName);
    }

    [Fact]
    public async Task GetProfileAsync_ShouldSucceed_WhenAccountIsPending()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "pending@warptalk.vn",
            FullName = "Pending User",
            IsActive = true,
            IsLocked = false,
            EmailVerified = false // Pending
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _profileService.GetProfileAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Pending User", result.Value.FullName);
    }

    [Fact]
    public async Task GetProfileAsync_ShouldBlock_WhenAccountIsDisabled()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "disabled@warptalk.vn",
            IsActive = false, // Disabled
            IsLocked = false,
            EmailVerified = true
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _profileService.GetProfileAsync(userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountInactive, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountInactive, result.Error);
    }

    [Fact]
    public async Task GetProfileAsync_ShouldBlock_WhenAccountIsLockedIndefinitely()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "locked@warptalk.vn",
            IsActive = true,
            IsLocked = true, // Locked
            EmailVerified = true
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _profileService.GetProfileAsync(userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountLocked, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountLockedIndefinitely, result.Error);
    }

    [Fact]
    public async Task GetProfileAsync_ShouldBlock_WhenAccountIsLockedUntilFuture()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var lockedUntil = DateTime.UtcNow.AddMinutes(15);
        var user = new User
        {
            Id = userId,
            Email = "locked-temp@warptalk.vn",
            IsActive = true,
            IsLocked = false,
            LockedUntil = lockedUntil, // Locked temporarily
            EmailVerified = true
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _profileService.GetProfileAsync(userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountLocked, result.ErrorCode);
        Assert.Contains(string.Format(AuthConstants.ErrorAccountLocked, ""), result.Error);
    }

    #endregion

    #region UpdateProfileAsync Tests

    [Fact]
    public async Task UpdateProfileAsync_ShouldSucceed_WhenAccountIsActive()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "active@warptalk.vn",
            FullName = "Old Name",
            IsActive = true,
            IsLocked = false,
            EmailVerified = true
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new UpdateProfileRequest("New Name", "0987654321", "en-US", "UTC");

        // Act
        var result = await _profileService.UpdateProfileAsync(userId, request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("New Name", result.Value.FullName);
        Assert.Equal("0987654321", result.Value.Phone);
        _userRepository.Received(1).Update(user);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateProfileAsync_ShouldBlock_WhenAccountIsPending()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "pending@warptalk.vn",
            FullName = "Pending User",
            IsActive = true,
            IsLocked = false,
            EmailVerified = false // Pending
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new UpdateProfileRequest("New Name", "0987654321", "en-US", "UTC");

        // Act
        var result = await _profileService.UpdateProfileAsync(userId, request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountPending, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountPending, result.Error);
        _userRepository.DidNotReceiveWithAnyArgs().Update(default!);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task UpdateProfileAsync_ShouldBlock_WhenAccountIsDisabled()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "disabled@warptalk.vn",
            IsActive = false,
            IsLocked = false,
            EmailVerified = true
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new UpdateProfileRequest("New Name", "0987654321", "en-US", "UTC");

        // Act
        var result = await _profileService.UpdateProfileAsync(userId, request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountInactive, result.ErrorCode);
        _userRepository.DidNotReceiveWithAnyArgs().Update(default!);
    }

    [Fact]
    public async Task UpdateProfileAsync_ShouldBlock_WhenAccountIsLocked()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "locked@warptalk.vn",
            IsActive = true,
            IsLocked = true,
            EmailVerified = true
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new UpdateProfileRequest("New Name", "0987654321", "en-US", "UTC");

        // Act
        var result = await _profileService.UpdateProfileAsync(userId, request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountLocked, result.ErrorCode);
        _userRepository.DidNotReceiveWithAnyArgs().Update(default!);
    }

    #endregion

    #region ChangePasswordAsync Tests

    [Fact]
    public async Task ChangePasswordAsync_ShouldSucceed_WhenAccountIsActive()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "active@warptalk.vn",
            PasswordHash = "old_hashed_password",
            IsActive = true,
            IsLocked = false,
            EmailVerified = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify("CurrentPassword", "old_hashed_password").Returns(true);
        _passwordHasher.Hash("NewPassword").Returns("new_hashed_password");

        var request = new ChangePasswordRequest("CurrentPassword", "NewPassword");

        // Act
        var result = await _profileService.ChangePasswordAsync(userId, request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("new_hashed_password", user.PasswordHash);
        _userRepository.Received(1).Update(user);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangePasswordAsync_ShouldBlock_WhenAccountIsPending()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "pending@warptalk.vn",
            IsActive = true,
            IsLocked = false,
            EmailVerified = false // Pending
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new ChangePasswordRequest("CurrentPassword", "NewPassword");

        // Act
        var result = await _profileService.ChangePasswordAsync(userId, request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountPending, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorAccountPending, result.Error);
        _userRepository.DidNotReceiveWithAnyArgs().Update(default!);
    }

    [Fact]
    public async Task ChangePasswordAsync_ShouldBlock_WhenAccountIsDisabled()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "disabled@warptalk.vn",
            IsActive = false,
            IsLocked = false,
            EmailVerified = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new ChangePasswordRequest("CurrentPassword", "NewPassword");

        // Act
        var result = await _profileService.ChangePasswordAsync(userId, request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountInactive, result.ErrorCode);
        _userRepository.DidNotReceiveWithAnyArgs().Update(default!);
    }

    [Fact]
    public async Task ChangePasswordAsync_ShouldBlock_WhenAccountIsLocked()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "locked@warptalk.vn",
            IsActive = true,
            IsLocked = true,
            EmailVerified = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new ChangePasswordRequest("CurrentPassword", "NewPassword");

        // Act
        var result = await _profileService.ChangePasswordAsync(userId, request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.AccountLocked, result.ErrorCode);
        _userRepository.DidNotReceiveWithAnyArgs().Update(default!);
    }

    #endregion

    #region Soft Delete Edge Cases

    [Fact]
    public async Task GetProfileAsync_ShouldBlock_WhenAccountIsSoftDeleted()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "deleted@warptalk.vn",
            IsActive = true,
            IsLocked = false,
            EmailVerified = true,
            DeletedAt = DateTime.UtcNow // Soft deleted
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Act
        var result = await _profileService.GetProfileAsync(userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.UserNotFound, result.ErrorCode);
        Assert.Equal(AuthConstants.ErrorUserNotFound, result.Error);
    }

    [Fact]
    public async Task UpdateProfileAsync_ShouldBlock_WhenAccountIsSoftDeleted()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "deleted@warptalk.vn",
            IsActive = true,
            IsLocked = false,
            EmailVerified = true,
            DeletedAt = DateTime.UtcNow // Soft deleted
        };

        _userRepository.GetByIdWithRolesAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new UpdateProfileRequest("New Name", "0987654321", "en-US", "UTC");

        // Act
        var result = await _profileService.UpdateProfileAsync(userId, request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.UserNotFound, result.ErrorCode);
        _userRepository.DidNotReceiveWithAnyArgs().Update(default!);
    }

    [Fact]
    public async Task ChangePasswordAsync_ShouldBlock_WhenAccountIsSoftDeleted()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "deleted@warptalk.vn",
            IsActive = true,
            IsLocked = false,
            EmailVerified = true,
            DeletedAt = DateTime.UtcNow // Soft deleted
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var request = new ChangePasswordRequest("CurrentPassword", "NewPassword");

        // Act
        var result = await _profileService.ChangePasswordAsync(userId, request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.UserNotFound, result.ErrorCode);
        _userRepository.DidNotReceiveWithAnyArgs().Update(default!);
    }

    #endregion
}
