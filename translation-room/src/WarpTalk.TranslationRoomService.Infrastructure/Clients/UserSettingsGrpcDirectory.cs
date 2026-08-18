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

    public async Task<UserVoicePreference?> GetVoicePreferenceAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetUserSettingsAsync(
                new GetUserRequest { Id = userId.ToString() },
                cancellationToken: ct);

            return response.Found ? new UserVoicePreference(response.VoiceCloneEnabled) : null;
        }
        catch (RpcException)
        {
            // Null, not false-as-a-value: "we could not ask" and "they said no" are different
            // facts, and only the caller knows that both currently lead to the same route. An
            // exception escaping here would fail route generation outright, which would cost the
            // room its translation over a preference lookup.
            return null;
        }
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
