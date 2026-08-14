namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public sealed record UserLanguageDefaults(
    string DefaultSpeakLanguage,
    string DefaultListenLanguage);

/// <summary>
/// WT-401: whether this person would like to be dubbed in their own voice, by default.
///
/// A WISH, not a permission. IVoiceConsentDirectory answers whether cloning their voice is
/// ALLOWED, and that gate is unchanged — this only decides whether a new audio route starts
/// life with the box already ticked.
///
/// Separate from GetDefaultsAsync rather than another field on UserLanguageDefaults: that
/// record is asked for on room creation and on every join, where the answer is not wanted, and
/// this one is asked once per speaker when routes are built. Same RPC underneath either way.
/// </summary>
public sealed record UserVoicePreference(bool VoiceCloneEnabled);

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

    /// <summary>
    /// WT-401. Returns null when the preference cannot be read (unknown user, guest, Auth
    /// unreachable) — callers must treat that as "no preference expressed" and leave the route
    /// off, never as a reason to fail. A preference that defaults ON when Auth is down would
    /// start cloning voices during an outage.
    /// </summary>
    Task<UserVoicePreference?> GetVoicePreferenceAsync(
        Guid userId,
        CancellationToken ct = default);
}
