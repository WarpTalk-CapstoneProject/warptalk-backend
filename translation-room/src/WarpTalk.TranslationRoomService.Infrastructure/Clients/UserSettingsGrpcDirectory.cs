using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Clients;

public sealed class UserSettingsGrpcDirectory : IUserSettingsDirectory
{
    private readonly UserService.UserServiceClient _client;

    public UserSettingsGrpcDirectory(UserService.UserServiceClient client)
    {
        _client = client;
    }

    public async Task<UserLanguageDefaults?> GetDefaultsAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var response = await _client.GetUserSettingsAsync(
            new GetUserRequest { Id = userId.ToString() },
            cancellationToken: ct);

        return response.Found
            ? new UserLanguageDefaults(
                response.DefaultSpeakLanguage,
                response.DefaultListenLanguage)
            : null;
    }
}
