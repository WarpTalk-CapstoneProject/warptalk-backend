using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

public class UserSettingsServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IUserSettingRepository _userSettingRepository;
    private readonly UserSettingsService _userSettingsService;

    public UserSettingsServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userRepository = Substitute.For<IUserRepository>();
        _userSettingRepository = Substitute.For<IUserSettingRepository>();

        _unitOfWork.UserRepository.Returns(_userRepository);
        _unitOfWork.UserSettingRepository.Returns(_userSettingRepository);

        _userSettingsService = new UserSettingsService(
            _unitOfWork,
            Substitute.For<ILogger<UserSettingsService>>()
        );
    }

    #region GetSettingsAsync Tests

    [Fact]
    public async Task GetSettingsAsync_ShouldReturnSettings_WhenExists()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "user@warptalk.vn",
            IsActive = true,
            EmailVerified = true
        };

        var settings = new UserSetting
        {
            UserId = userId,
            DefaultSpeakLanguage = "vi-VN",
            DefaultListenLanguage = "en-US",
            Theme = "dark",
            TranscriptFontSize = 14,
            DefaultTranslationRoomType = "group",
            DefaultMaxParticipants = 10,
            UpdatedAt = DateTime.UtcNow
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _userSettingRepository.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(settings);

        // Act
        var result = await _userSettingsService.GetSettingsAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("vi-VN", result.Value.DefaultSpeakLanguage);
        Assert.Equal("dark", result.Value.Theme);
    }

    [Fact]
    public async Task GetSettingsAsync_ShouldCreateDefaultSettings_WhenNotExists()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "legacy@warptalk.vn",
            IsActive = true,
            EmailVerified = true
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _userSettingRepository.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns((UserSetting)null!);

        // Act
        var result = await _userSettingsService.GetSettingsAsync(userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("vi-VN", result.Value.DefaultSpeakLanguage); // Default Speak Language
        Assert.Equal("en-US", result.Value.DefaultListenLanguage); // Default Listen Language
        Assert.Equal("system", result.Value.Theme);
        Assert.Equal(14, result.Value.TranscriptFontSize);
        Assert.Equal("instant", result.Value.DefaultTranslationRoomType);

        _userSettingRepository.Received(1).Add(Arg.Is<UserSetting>(s => s.UserId == userId));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    #endregion

    #region UpdateSettingsAsync Tests

    [Fact]
    public async Task UpdateSettingsAsync_ShouldSucceed_WhenPayloadValid()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "user@warptalk.vn",
            IsActive = true,
            EmailVerified = true
        };

        var settings = new UserSetting
        {
            UserId = userId,
            DefaultSpeakLanguage = "vi-VN",
            DefaultListenLanguage = "en-US",
            Theme = "light",
            TranscriptFontSize = 14,
            DefaultTranslationRoomType = "group",
            DefaultMaxParticipants = 10,
            UpdatedAt = DateTime.UtcNow
        };

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _userSettingRepository.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(settings);

        var request = new UpdateUserSettingsRequest(
            DefaultSpeakLanguage: "en-US",
            DefaultListenLanguage: "ja-JP",
            Theme: "dark",
            TranscriptFontSize: 16,
            DefaultMaxParticipants: 50
        );

        // Act
        var result = await _userSettingsService.UpdateSettingsAsync(userId, request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("en-US", result.Value.DefaultSpeakLanguage);
        Assert.Equal("ja-JP", result.Value.DefaultListenLanguage);
        Assert.Equal("dark", result.Value.Theme);
        Assert.Equal(16, result.Value.TranscriptFontSize);
        Assert.Equal(50, result.Value.DefaultMaxParticipants);

        _userSettingRepository.Received(1).Update(settings);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    #endregion
}
