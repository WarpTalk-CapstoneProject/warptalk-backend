using Grpc.Core;
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

    public async Task<string?> GetDisplayNameAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetUserByIdAsync(
                new GetUserRequest { Id = userId.ToString() },
                cancellationToken: ct);

            return string.IsNullOrWhiteSpace(response.FullName) ? null : response.FullName;
        }
        catch (RpcException)
        {
            // Unlike GetUserSettings, which answers with Found = false, GetUserById signals an
            // unknown id by throwing NOT_FOUND (AuthService UserServiceGrpc.GetUserById). An
            // unresolvable name is "unknown", not an error the caller should have to handle.
            return null;
        }
    }
}
