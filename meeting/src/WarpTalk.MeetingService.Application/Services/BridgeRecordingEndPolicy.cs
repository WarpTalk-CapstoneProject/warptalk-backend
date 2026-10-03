using System.Text.Json;
using WarpTalk.MeetingService.Application.Interfaces;

namespace WarpTalk.MeetingService.Application.Services;

/// <summary>
/// The pure half of "stop a Google Meet bridge recording when the bridge session ends".
///
/// THE BUG
///   A bridge recording (RoomComposite egress of an EXTERNAL_BRIDGE room) was only ever stopped by
///   somebody pressing Stop (SetRecordingAsync "stop"). When the bridge session ended — the capturer
///   left Meet, the 30 s meet-follow countdown made their desktop leave, MeetConferenceEndWorker
///   ended the room and every client left on RoomEnded, or the desktop simply closed — nothing
///   stopped the egress. LiveKit ended it only when it closed the empty room after its departure
///   timeout (20 s by default), so every such file finished with ~20-24 s of nothing.
///
/// THE SIGNAL: THE ROOM HAS NOBODY IN IT
///   Not "the host left": a bridge room is shared by every WarpTalk user in the Meet call, so the
///   host leaving while the capturer (or anybody else) is still there is not the end of anything.
///   Not "the capturer left" either: a takeover moves capture to someone else's desktop. What the
///   reported tail actually is, is a room LiveKit is about to close because it is empty. So the
///   recording is stopped when LiveKit itself says no person and no bridge stand-in is left
///   (<see cref="CountsAsPresence"/>) — the moment LiveKit starts its own departure countdown.
///
/// THE GRACE: <see cref="DefaultGrace"/> AFTER THE LAST DEPARTURE
///   A page reload, a desktop restart or a "leave and come straight back" also empties the room for
///   a few seconds, and must not cut the recording: after a reload the web never auto-starts a
///   bridge recording again (no consent answer in that page — bridge-recording.ts "no-choice"), so a
///   cut here would lose the rest of the call. LiveKit already gives such a reload 20 s (its
///   departure timeout) before the room and the egress die anyway; the grace only has to be
///   shorter than that to be worth anything, and long enough to cover a reload. 10 s does both:
///   a reload/rejoin is typically 2-6 s (page load, join API, LiveKit connect), and the tail drops
///   from ~20-24 s to ~10-12 s. Measured from the LATEST departure in the room, so two people
///   leaving a few seconds apart each get the full grace.
///
/// NO GRACE WHEN THE ROOM HAS ENDED
///   When translation-room already says the room is over (ENDED/FINISHED/CANCELLED/EXPIRED —
///   MeetConferenceEndWorker, the idle reaper, End for everyone), nobody is coming back, so the
///   first departure stops the recording at once, whoever is still connected.
/// </summary>
public static class BridgeRecordingEndPolicy
{
    /// <summary>See THE GRACE above. Overridable with Meeting:BridgeRecording:EndGraceSeconds.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);

    /// <summary>Lower/upper bounds applied to a configured grace. Above ~20 s LiveKit closes the room first.</summary>
    public static readonly TimeSpan MinGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Identities that are ours, not a person's: the ingress bot, the TTS interpreters (the same list
    /// as MeetingWebhookService's BotIdentityPrefixes), and LiveKit's own egress recorder ("EG_…").
    /// None of them keeps a meeting alive — the recorder above all, which is the participant this
    /// whole check exists to stop.
    /// </summary>
    private static readonly string[] NonPersonIdentityPrefixes = ["AIBot_", "ai-interpreter-", "EG_"];

    /// <summary>LiveKit participant kinds that are machinery, not people (SIP callers ARE people).</summary>
    private static readonly HashSet<string> NonPersonKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "INGRESS", "EGRESS", "AGENT",
    };

    private static readonly HashSet<string> EndedRoomStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENDED", "FINISHED", "CANCELLED", "EXPIRED",
    };

    /// <summary>
    /// Whether a participant LiveKit lists keeps the bridge session alive. A person (STANDARD or SIP)
    /// does, and so does the bridge stand-in (ExternalBridgeConstants.ParticipantUserId): while it is
    /// connected the capturer's desktop is still carrying the far side of the call. A participant
    /// LiveKit already marks DISCONNECTED does not.
    /// </summary>
    public static bool CountsAsPresence(LiveKitRoomParticipant participant)
    {
        if (string.Equals(participant.State, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsPerson(participant.Identity, participant.Kind);
    }

    /// <summary>The identity/kind half of <see cref="CountsAsPresence"/>, also used on the webhook's leaver.</summary>
    public static bool IsPerson(string? identity, string? kind)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return false;
        if (NonPersonIdentityPrefixes.Any(prefix => identity.StartsWith(prefix, StringComparison.Ordinal)))
            return false;
        return kind is null || !NonPersonKinds.Contains(kind);
    }

    /// <summary>A translation-room status after which nobody can rejoin.</summary>
    public static bool IsEndedRoomStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) && EndedRoomStatuses.Contains(status);

    /// <summary>
    /// Whether LiveKit's EgressInfo says the egress is still capturing (STARTING or ACTIVE). ENDING
    /// means somebody already stopped it — the Stop button, or LiveKit closing the room — and a
    /// second StopEgress would only be refused. Enum name or ordinal, as EgressReconciliationService.
    /// </summary>
    public static bool IsEgressCapturing(JsonElement egressInfo)
    {
        if (!egressInfo.TryGetProperty("status", out var status))
            return true; // proto3 omits the zero value: no status is EGRESS_STARTING.

        if (status.ValueKind == JsonValueKind.String)
        {
            var name = status.GetString();
            return string.Equals(name, "EGRESS_STARTING", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "EGRESS_ACTIVE", StringComparison.OrdinalIgnoreCase);
        }

        return status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var ordinal)
            && ordinal is 0 or 1;
    }

    /// <summary>A configured grace in seconds, clamped to [<see cref="MinGrace"/>, <see cref="MaxGrace"/>].</summary>
    public static TimeSpan GraceFromSeconds(string? configured)
    {
        if (!double.TryParse(configured, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return DefaultGrace;

        var grace = TimeSpan.FromSeconds(seconds);
        if (grace < MinGrace) return MinGrace;
        if (grace > MaxGrace) return MaxGrace;
        return grace;
    }
}
