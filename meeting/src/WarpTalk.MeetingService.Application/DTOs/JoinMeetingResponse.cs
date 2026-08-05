namespace WarpTalk.MeetingService.Application.DTOs;

public class JoinMeetingResponse
{
    public string Token { get; set; } = string.Empty;
    public string ProviderRoomName { get; set; } = string.Empty;
    public string ParticipantIdentity { get; set; } = string.Empty;
    public bool IsWaitingRoom { get; set; } = false;

    /// <summary>WT-04: the room's mute-on-entry setting — the frontend defaults the local
    /// mic to muted on first mount when this is true.</summary>
    public bool MuteOnEntry { get; set; } = false;

    /// <summary>WT-282: the room's lock setting — the frontend renders the true state in the
    /// host-controls menu on first open instead of assuming a default. The server already
    /// enforces this on join (see JoinMeetingAsync); this only reports it back.</summary>
    public bool Locked { get; set; } = false;

    /// <summary>WT-283: whether the room is being recorded right now, so a joining participant
    /// is told before they speak. Derived from MeetingRoom.ActiveEgressId being non-empty —
    /// the same derivation SetRecordingAsync uses.</summary>
    /// <remarks>
    /// Contract decision (WT-283): this is a DERIVED BOOL for EVERY joiner, and the egress id is
    /// deliberately NOT carried here.
    ///
    /// Being recorded is a participant-facing fact, not a host convenience, so something has to
    /// reach everyone — a host-only field would leave ordinary participants unable to know. That
    /// rules out shipping nothing and rules out making the field conditional on the caller's role
    /// (which nothing else in this DTO does, and which would make the response shape depend on who
    /// asked).
    ///
    /// Embedding RecordingStateDto would satisfy the same need but would also hand every
    /// participant ActiveEgressId, an internal LiveKit Egress job handle they cannot act on. That
    /// costs privacy of infrastructure detail and buys nothing, because no client needs the id:
    ///  - Stopping a recording is POST rooms/{id}/recording with body {"action":"stop"} only. The
    ///    server resolves meetingRoom.ActiveEgressId itself (MeetingRoomService.SetRecordingAsync),
    ///    so the id is never a client-supplied input.
    ///  - A host that starts a recording already receives the id in that call's own
    ///    RecordingStateDto response, which is the only place any client gets it today.
    /// So the bool is strictly sufficient, and the omission of the id costs the host UI nothing.
    /// </remarks>
    public bool Recording { get; set; } = false;
}
