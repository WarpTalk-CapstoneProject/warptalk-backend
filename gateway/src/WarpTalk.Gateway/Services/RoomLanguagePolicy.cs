using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// "May this participant put themselves on this language, in this room?" — for the SignalR hub.
///
/// TWO LIMITS, ONE LOOKUP (WT-709)
///   Languages narrow: the workspace's whitelist (L1) contains the meeting's declared languages
///   (L2 — its source plus its targets) contains what its artifacts may be generated in. This
///   answers against BOTH, in one pass, because the room RPC below already carries the room's own
///   languages (WT-704 put `source_language` and `target_languages` on the response) and the
///   caller is about to write a value that has to satisfy both.
///
///   The L2 half is the rule WT-709 restores. Until then a participant could sit in a vi/en
///   meeting speaking Korean as long as the WORKSPACE permitted Korean somewhere — so the
///   meeting's stored transcript held languages the meeting never declared, and the artifact
///   rules, which narrow FROM L2, had nothing honest to narrow from. The way out is not to widen
///   the rule but to let the HOST widen the ROOM: POST /translation-rooms/{id}/languages adds a
///   language mid-meeting, and a `RoomLanguagesChanged` broadcast tells every picker about it.
///
///   The verdicts stay apart because the two refusals have different exits. "Not in this
///   meeting's languages" is answered by asking the host, who can fix it from inside the call;
///   "not permitted by this workspace" is answered by an Owner changing a workspace setting, and
///   telling a guest to ask the host about that one sends them on an errand that cannot succeed.
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
public enum RoomLanguageVerdict
{
    /// <summary>Inside the meeting's declared languages and inside the workspace's whitelist.</summary>
    Allowed,

    /// <summary>
    /// The MEETING does not declare this language. The host can add it without leaving the call,
    /// so the refusal names that way out.
    /// </summary>
    NotInRoomLanguages,

    /// <summary>
    /// The WORKSPACE does not permit this language at all, so no host action inside the meeting
    /// can make it available.
    /// </summary>
    NotInWorkspacePolicy
}

public interface IRoomLanguagePolicy
{
    /// <summary>
    /// Whether this participant may put themselves on this language, and if not, which limit says
    /// no. Locale tags are accepted: `vi-VN` and `vi` are the same answer, because rooms store
    /// tags and the policies store bare codes.
    /// </summary>
    Task<RoomLanguageVerdict> EvaluateLanguageAsync(Guid translationRoomId, string language, CancellationToken ct = default);
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

    public async Task<RoomLanguageVerdict> EvaluateLanguageAsync(
        Guid translationRoomId,
        string language,
        CancellationToken ct = default)
    {
        var normalized = BaseLanguage(language);
        if (normalized.Length == 0)
        {
            // The hub rejects blank input before it gets here; nothing to judge.
            return RoomLanguageVerdict.Allowed;
        }

        GetTranslationRoomResponse room;
        try
        {
            room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = translationRoomId.ToString() },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RoomLanguagePolicy: could not resolve room {RoomId} to check its language policy; allowing {Language}.",
                translationRoomId,
                normalized);
            return RoomLanguageVerdict.Allowed;
        }

        // WT-709: L2 first, and not only because it is free once the room is in hand. It is the
        // narrower of the two limits — a room's languages were themselves validated against the
        // workspace whitelist when they were saved — so a language that fails here is the common
        // case, and answering it without a second RPC keeps the usual refusal to one hop.
        //
        // EMPTY MEANS UNKNOWN, exactly as it does for the workspace list below. proto3 cannot tell
        // an empty repeated field from an absent one, so a room with no languages on the wire is
        // either a fixture, an external bridge, or a server that predates WT-704 — none of which
        // is a statement that this meeting permits nothing.
        var roomLanguages = new List<string> { BaseLanguage(room.SourceLanguage) }
            .Concat(room.TargetLanguages.Select(BaseLanguage))
            .Where(code => code.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        // An EXTERNAL_BRIDGE room has no L2 to hold anyone to: it is one shared room per Meet
        // code, joined with whatever language each WarpTalk user picked in the popup, and its
        // far-side stand-in speaks whatever the Meet side speaks. Only the workspace limit below
        // applies there — the same carve-out translation-room's LanguagePolicy.DeclaredLanguages
        // makes for the REST join.
        var isBridge = string.Equals(room.TranslationRoomType, "EXTERNAL_BRIDGE", StringComparison.OrdinalIgnoreCase);

        if (!isBridge && roomLanguages.Count > 0 && !roomLanguages.Contains(normalized))
        {
            return RoomLanguageVerdict.NotInRoomLanguages;
        }

        if (!Guid.TryParse(room.WorkspaceId, out var workspaceId) || workspaceId == Guid.Empty)
        {
            // A room with no workspace is a bridge or a fixture, not a policy violation.
            return RoomLanguageVerdict.Allowed;
        }

        try
        {
            var settings = await _workspaceClient.GetWorkspaceSettingsAsync(
                new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);

            if (settings.AllowedTargetLanguages.Count == 0)
            {
                return RoomLanguageVerdict.Allowed;
            }

            return settings.AllowedTargetLanguages.Any(allowed => BaseLanguage(allowed) == normalized)
                ? RoomLanguageVerdict.Allowed
                : RoomLanguageVerdict.NotInWorkspacePolicy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RoomLanguagePolicy: could not read the language policy for workspace {WorkspaceId}; allowing {Language} in room {RoomId}.",
                workspaceId,
                normalized,
                translationRoomId);
            return RoomLanguageVerdict.Allowed;
        }
    }
}
