using NSubstitute;
using WarpTalk.AuthService.API.GrpcServices;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AuthService.Tests;

public class UserServiceGrpcTests
{
    [Fact]
    public async Task GetUserSettings_ReturnsAuthoritativeLanguageDefaults()
    {
        var userId = Guid.NewGuid();
        var unitOfWork = Substitute.For<IUnitOfWork>();
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

        var service = new UserServiceGrpc(unitOfWork);

        var response = await service.GetUserSettings(
            new GetUserRequest { Id = userId.ToString() },
            null!);

        Assert.True(response.Found);
        Assert.Equal("vi-VN", response.DefaultSpeakLanguage);
        Assert.Equal("en-US", response.DefaultListenLanguage);
    }
}
