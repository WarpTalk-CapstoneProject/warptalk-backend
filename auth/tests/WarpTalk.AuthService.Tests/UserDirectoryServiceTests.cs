using NSubstitute;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;

namespace WarpTalk.AuthService.Tests;

public class UserDirectoryServiceTests
{
    private static (UserDirectoryService Service, IUnitOfWork UnitOfWork) CreateService()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        return (new UserDirectoryService(unitOfWork), unitOfWork);
    }

    // ── WT-699 / TC4104: the BROADCAST audience ──────────────────────────────────────

    [Fact]
    public async Task ListActiveUserIdsAsync_HandsBackACursorWhileThePageIsFull()
    {
        var (service, unitOfWork) = CreateService();
        var users = Substitute.For<IUserRepository>();
        unitOfWork.UserRepository.Returns(users);
        var page = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        users.GetActiveUserIdsPageAsync(null, 2, Arg.Any<CancellationToken>()).Returns(page);

        var result = await service.ListActiveUserIdsAsync(null, 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(page, result.Value.UserIds);
        Assert.Equal(page[^1], result.Value.NextAfterId);
    }

    [Fact]
    public async Task ListActiveUserIdsAsync_EndsOnAShortPage_AndClampsThePageSize()
    {
        var (service, unitOfWork) = CreateService();
        var users = Substitute.For<IUserRepository>();
        unitOfWork.UserRepository.Returns(users);
        var after = Guid.NewGuid();
        users.GetActiveUserIdsPageAsync(after, UserDirectoryService.MaxUserIdPageSize, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { Guid.NewGuid() });

        var result = await service.ListActiveUserIdsAsync(after, 50_000);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.UserIds);
        Assert.Null(result.Value.NextAfterId);
    }

    [Fact]
    public async Task GetLanguageDefaultsAsync_ReturnsStoredLanguages()
    {
        var userId = Guid.NewGuid();
        var (service, unitOfWork) = CreateService();
        var settingsRepository = Substitute.For<IUserSettingRepository>();
        unitOfWork.UserSettingRepository.Returns(settingsRepository);
        settingsRepository
            .GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserSetting
            {
                UserId = userId,
                DefaultSpeakLanguage = "vi-VN",
                DefaultListenLanguage = "en-US"
            });

        var result = await service.GetLanguageDefaultsAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("vi-VN", result.Value!.DefaultSpeakLanguage);
        Assert.Equal("en-US", result.Value.DefaultListenLanguage);
    }

    [Fact]
    public async Task GetLanguageDefaultsAsync_FallsBackWhenLanguagesUnset()
    {
        var userId = Guid.NewGuid();
        var (service, unitOfWork) = CreateService();
        var settingsRepository = Substitute.For<IUserSettingRepository>();
        unitOfWork.UserSettingRepository.Returns(settingsRepository);
        settingsRepository
            .GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserSetting
            {
                UserId = userId,
                // The columns are non-nullable in the model, but rows written before the
                // defaults existed can still materialise as null — that is what the
                // service's fallback guards against.
                DefaultSpeakLanguage = null!,
                DefaultListenLanguage = null!
            });

        var result = await service.GetLanguageDefaultsAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("vi-VN", result.Value!.DefaultSpeakLanguage);
        Assert.Equal("en-US", result.Value.DefaultListenLanguage);
    }

    [Fact]
    public async Task GetLanguageDefaultsAsync_SucceedsWithNull_WhenNoSettingsRow()
    {
        var userId = Guid.NewGuid();
        var (service, unitOfWork) = CreateService();
        var settingsRepository = Substitute.For<IUserSettingRepository>();
        unitOfWork.UserSettingRepository.Returns(settingsRepository);
        settingsRepository
            .GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns((UserSetting?)null);

        var result = await service.GetLanguageDefaultsAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetUserByIdAsync_FailsWhenUserMissing()
    {
        var userId = Guid.NewGuid();
        var (service, unitOfWork) = CreateService();
        var userRepository = Substitute.For<IUserRepository>();
        unitOfWork.UserRepository.Returns(userRepository);
        userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await service.GetUserByIdAsync(userId);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task GetUserByEmailAsync_RejectsBlankEmailWithoutTouchingRepository()
    {
        var (service, unitOfWork) = CreateService();
        var userRepository = Substitute.For<IUserRepository>();
        unitOfWork.UserRepository.Returns(userRepository);

        var result = await service.GetUserByEmailAsync("   ");

        Assert.False(result.IsSuccess);
        await userRepository.DidNotReceiveWithAnyArgs().FirstOrDefaultAsync(default!, default!, default);
    }
}
