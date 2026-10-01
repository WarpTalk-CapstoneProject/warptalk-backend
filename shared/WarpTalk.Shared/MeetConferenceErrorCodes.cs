namespace WarpTalk.Shared;

/// <summary>
/// The in-band <c>error_code</c> values of <c>meet_conference.proto</c>, and the error codes the
/// Meet REST surfaces answer HTTP callers with. One spelling, because the callers branch on them:
/// AssistantService produces them, TranslationRoomService's conference-end worker and
/// TranscriptService's relabel job decide what to do from them.
/// </summary>
public static class MeetConferenceErrorCodes
{
    /// <summary>
    /// The user's Google grant does not include <see cref="MeetSpaceReadonlyScope"/> (or Google
    /// answered 403 insufficient scope). Not a fault: the host simply has not granted it.
    /// </summary>
    public const string MeetScopeMissing = "meet_scope_missing";

    /// <summary>No live Google connection for the user — never connected, revoked or expired.</summary>
    public const string ConnectionRequired = "connection_required";

    /// <summary>The meeting reference is not a Meet code or a meet.google.com link.</summary>
    public const string InvalidMeeting = "invalid_meeting";

    /// <summary>Google refused the read for a reason other than scope (403 PERMISSION_DENIED).</summary>
    public const string PermissionDenied = "meet_permission_denied";

    /// <summary>Google answered 404 for a resource that was named explicitly.</summary>
    public const string NotFound = "not_found";

    /// <summary>Google rate-limited the call. Transient.</summary>
    public const string ProviderRateLimited = "provider_rate_limited";

    /// <summary>Google or the network failed. Transient.</summary>
    public const string ProviderUnavailable = "provider_unavailable";

    /// <summary>The OAuth scope the google_meet plugin needs for conference records.</summary>
    public const string MeetSpaceReadonlyScope = "https://www.googleapis.com/auth/meetings.space.readonly";

    /// <summary>
    /// The display name written to a transcript segment spoken by the bridge stand-in until (and
    /// unless) the post-meeting relabel attributes it to a named Google Meet participant.
    /// </summary>
    public const string MeetSideFallbackSpeakerName = "Google Meet participants";
}
