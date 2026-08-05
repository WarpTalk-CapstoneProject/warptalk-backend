namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public sealed record UserLanguageDefaults(
    string DefaultSpeakLanguage,
    string DefaultListenLanguage);

public interface IUserSettingsDirectory
{
    Task<UserLanguageDefaults?> GetDefaultsAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// WT-281: the user's own display name, so the host row this service seeds is labelled with a
    /// person instead of the literal string "Host".
    ///
    /// Deliberately hung off this existing directory rather than a new client: the implementation
    /// already holds the Auth <c>UserService</c> gRPC channel that room creation uses for language
    /// defaults, and <c>GetUserById</c> is an RPC that channel already exposes. No new dependency,
    /// no new registration, no new proto.
    ///
    /// Returns null when the name cannot be resolved (unknown user, Auth unreachable). Callers must
    /// treat that as "unknown", never as a failure — a missing name is not a reason to refuse to
    /// create a room.
    /// </summary>
    Task<string?> GetDisplayNameAsync(
        Guid userId,
        CancellationToken ct = default);
}
