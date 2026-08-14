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
                new UserLanguageDefaultsDto("vi-VN", "en-US", VoiceCloneEnabled: true)));

        var service = new UserServiceGrpc(userDirectory, Substitute.For<IVoiceConsentService>());

        var response = await service.GetUserSettings(
            new GetUserRequest { Id = userId.ToString() },
            null!);

        Assert.True(response.Found);
        Assert.Equal("vi-VN", response.DefaultSpeakLanguage);
        Assert.Equal("en-US", response.DefaultListenLanguage);
        // WT-401: the preference has to survive the trip. It is the only way the switch in
        // Settings can reach a meeting, and dropping it here is invisible — the caller would
        // simply see "false" and leave voice cloning off for someone who asked for it.
        Assert.True(response.VoiceCloneEnabled);
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
