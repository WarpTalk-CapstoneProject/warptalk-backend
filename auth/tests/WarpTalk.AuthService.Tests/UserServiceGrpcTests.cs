using NSubstitute;
using WarpTalk.AuthService.API.GrpcServices;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AuthService.Tests;

public class UserServiceGrpcTests
{
    [Fact]
    public async Task GetUserSettings_ReturnsAuthoritativeLanguageDefaults()
    {
        var userId = Guid.NewGuid();
        var userDirectory = Substitute.For<IUserDirectoryService>();
        userDirectory
            .GetLanguageDefaultsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success<UserLanguageDefaultsDto?>(
                new UserLanguageDefaultsDto("vi-VN", "en-US")));

        var service = new UserServiceGrpc(userDirectory, Substitute.For<IVoiceConsentService>());

        var response = await service.GetUserSettings(
            new GetUserRequest { Id = userId.ToString() },
            null!);

        Assert.True(response.Found);
        Assert.Equal("vi-VN", response.DefaultSpeakLanguage);
        Assert.Equal("en-US", response.DefaultListenLanguage);
    }

    [Fact]
    public async Task GetUserSettings_ReturnsNotFound_WhenUserHasNoSettings()
    {
        var userId = Guid.NewGuid();
        var userDirectory = Substitute.For<IUserDirectoryService>();
        userDirectory
            .GetLanguageDefaultsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success<UserLanguageDefaultsDto?>(null));

        var service = new UserServiceGrpc(userDirectory, Substitute.For<IVoiceConsentService>());

        var response = await service.GetUserSettings(
            new GetUserRequest { Id = userId.ToString() },
            null!);

        Assert.False(response.Found);
    }
}
