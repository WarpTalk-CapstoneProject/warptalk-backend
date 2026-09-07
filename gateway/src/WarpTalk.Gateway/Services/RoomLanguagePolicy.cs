using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// "May this participant put themselves on this language, in this room?" — for the SignalR hub.
///
/// THE GAP THIS CLOSES
///   A workspace's `allowedTargetLanguages` was enforced in exactly one place on the server:
///   ValidateMeetingCreation, which runs once, when a room is created (WT-271), and later on the
///   room EDIT path (WT-466). Nothing checked it when a participant changed their own language
///   mid-meeting. SetSpeakLanguage and SetListenLanguage validated that the string was not
///   whitespace and wrote it straight into Redis.
///
///   Every language picker in the web client does narrow itself by the policy (WT-490, WT-497), so
///   the rule looked enforced from the inside. It was advisory: a participant whose workspace
///   settings failed to load — an external guest is not a member, so GET /workspaces/{id}/settings
///   answers 403 and the client reads the absent list as "unrestricted" — was offered every
///   language WarpTalk knows, and the hub accepted whichever one they picked. That is the reported
///   "workspace settings chọn 2 lang nhưng trong meet vẫn chọn được tiếng Nhật".
///
///   Fixing only the client would leave the hub open to a stale tab, the desktop app, and anyone
///   with the browser console. The rule belongs on the side that writes the value.
///
/// EMPTY MEANS UNRESTRICTED
///   The same convention every other reader of this list uses. A workspace that never set a policy
///   permits everything; reading "no entries" as "permit nothing" would silence every meeting in
///   every workspace that has not opted in.
///
/// WHY AN RPC FAILURE PERMITS RATHER THAN REFUSES
///   Deliberately the opposite of <see cref="RoomHostAuthority"/>, and the difference is what the
///   two rules protect. Host authority is a security boundary: failing open there lets any
///   participant mute the room, so it fails closed. This is a workspace PREFERENCE about which
///   languages a meeting works in. Failing closed here would mean that a WorkspaceService blip
///   freezes everyone on the language they happen to be on, in every room, for the length of the
///   outage — turning a preference into an outage. A logged warning and a permitted change is the
///   cheaper mistake, and it is the state the product was already in before this type existed.
///
///   Note the asymmetry that matters: a SUCCESSFUL lookup that does not contain the language is a
///   refusal. Only a thrown RPC permits.
///
/// NO CACHING
///   Changing your own language is a rare, human-initiated action, so one or two gRPC hops each is
///   cheap — the same reasoning RoomHostAuthority gives. A cache would also keep honouring a policy
///   an Owner had just tightened, which is the complaint that started this.
/// </summary>
public interface IRoomLanguagePolicy
{
    /// <summary>
    /// True when the room's workspace permits this language. Locale tags are accepted: `vi-VN` and
    /// `vi` are the same answer, because rooms store tags and the policy stores bare codes.
    /// </summary>
    Task<bool> IsLanguageAllowedAsync(Guid translationRoomId, string language, CancellationToken ct = default);
}

public sealed class RoomLanguagePolicy : IRoomLanguagePolicy
{
    private readonly Shared.Protos.TranslationRoomService.TranslationRoomServiceClient _roomClient;
    private readonly WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly ILogger<RoomLanguagePolicy> _logger;

    public RoomLanguagePolicy(
        Shared.Protos.TranslationRoomService.TranslationRoomServiceClient roomClient,
        WorkspaceService.WorkspaceServiceClient workspaceClient,
        ILogger<RoomLanguagePolicy> logger)
    {
        _roomClient = roomClient;
        _workspaceClient = workspaceClient;
        _logger = logger;
    }

    /// <summary>
    /// The primary subtag, lower-cased — the same reduction the hub applies before it stores a
    /// language and the same one `normalizeLanguagePolicy` applies on the web. Comparing tags
    /// verbatim is how "vi-VN" fails to match a policy that permits "vi".
    /// </summary>
    private static string BaseLanguage(string language) =>
        string.IsNullOrWhiteSpace(language) ? string.Empty : language.Split('-')[0].Trim().ToLowerInvariant();

    public async Task<bool> IsLanguageAllowedAsync(
        Guid translationRoomId,
        string language,
        CancellationToken ct = default)
    {
        var normalized = BaseLanguage(language);
        if (normalized.Length == 0)
        {
            // The hub rejects blank input before it gets here; nothing to judge.
            return true;
        }

        string workspaceIdRaw;
        try
        {
            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = translationRoomId.ToString() },
                cancellationToken: ct);
            workspaceIdRaw = room.WorkspaceId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RoomLanguagePolicy: could not resolve room {RoomId} to check its language policy; allowing {Language}.",
                translationRoomId,
                normalized);
            return true;
        }

        if (!Guid.TryParse(workspaceIdRaw, out var workspaceId))
        {
            // A room with no workspace is a bridge or a fixture, not a policy violation.
            return true;
        }

        try
        {
            var settings = await _workspaceClient.GetWorkspaceSettingsAsync(
                new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);

            if (settings.AllowedTargetLanguages.Count == 0)
            {
                return true;
            }

            return settings.AllowedTargetLanguages.Any(
                allowed => BaseLanguage(allowed) == normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RoomLanguagePolicy: could not read the language policy for workspace {WorkspaceId}; allowing {Language} in room {RoomId}.",
                workspaceId,
                normalized,
                translationRoomId);
            return true;
        }
    }
}
