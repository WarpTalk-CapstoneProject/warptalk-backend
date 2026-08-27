using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioRoutingEventType
{
    // Modern diagram-aligned events
    config_ready,
    session_starts,

    /// <summary>
    /// Translation was stopped while the MEETING carries on — the routes go back to READY and can
    /// be started again, and nothing about the room's own lifecycle changes.
    ///
    /// Distinct from <see cref="room_pause"/> on purpose. A pause means "stop listening": the AI
    /// workers treat a PAUSED room as one whose microphone must be ignored, so it also stops the
    /// transcript. Stopping translation must not, because transcription and translation are
    /// separate features and a room can legitimately run with only the first.
    /// </summary>
    translation_stopped,
    room_pause,
    room_resume,
    session_ends,
    system_disabled,
    flush_runtime,
    outputs_linked,
    finalization_failed,
    finalization_abandoned,

    stt_latency_high,
    stt_recovered,

    /// <summary>
    /// Speech-to-text is down for this room — stt_worker publishes it when its model call fails.
    ///
    /// WT-429. This member was missing while its recovery partner <see cref="stt_recovered"/> and
    /// its siblings <see cref="tts_unavailable"/> and <see cref="audio_unavailable"/> all existed,
    /// so every one of these events failed to parse and was dead-lettered: 381 of the 497 entries
    /// in translationRoom:system_events:dlq, across 83 rooms, all reading "Unknown event type."
    /// The backend was never told that transcription had stopped.
    /// </summary>
    stt_unavailable,
    translation_latency_high,
    translation_recovered,
    tts_latency_high,
    tts_recovered,

    voice_clone_unavailable,
    voice_clone_recovered,

    /// <summary>
    /// tts_worker finished cloning a speaker's voice and cached it. INFORMATIONAL — it announces
    /// that a clone now exists and moves no route between states.
    ///
    /// Deliberately NOT folded into <see cref="voice_clone_recovered"/>, which is the recovery half
    /// of <see cref="voice_clone_unavailable"/> and pulls a route out of STANDARD_VOICE back to
    /// BROADCASTING. This event's payload carries a speakerId — not a participantId or a userId —
    /// so AudioRouteEventProcessor cannot narrow it to one participant and falls through to every
    /// route in the room. Treating it as a recovery would let one speaker's clone finishing drag
    /// every other speaker's route out of its fallback.
    ///
    /// It was absent for the same reason stt_unavailable was. WT-429 added the two names it found
    /// in the dead-letter queue; tts_worker began publishing this one the day after that fix
    /// landed, so it inherited the identical "Unknown event type." failure and was dead-lettered
    /// 19 times between 16 and 20 Aug 2026. A hand-listed contract test cannot catch a name that
    /// did not exist when the list was written.
    /// </summary>
    voice_clone_ready,

    // Billing integration events
    token_exhausted,
    token_recovered,

    /// <summary>
    /// Somebody changed the language they speak or hear WHILE the meeting is running. WT-419.
    ///
    /// Not a state transition on an existing route, which is what every other member of this enum
    /// is — it changes the SHAPE of the mesh, so it is dispatched away from
    /// AudioRouteEventProcessor entirely (see ParticipantLanguageProcessor) and ends in a full
    /// GenerateRoutesAsync rather than a per-route transition.
    ///
    /// It exists because the gateway hub wrote language changes to Redis and nowhere else, while
    /// the mesh reads the participant row in Postgres. A route was therefore pinned to whatever
    /// languages the pair held at join time, forever.
    /// </summary>
    participant_language_changed,

    tts_unavailable,
    audio_unavailable,
    audio_recovered,
    telemetry_state_updated,

    /// <summary>
    /// tts_worker finished the last chunk of a segment. INFORMATIONAL — it reports progress and
    /// moves no route between states.
    ///
    /// WT-429. Recognised here only so it stops being an unknown event: it made up the other 116
    /// dead-lettered entries, each retried three times before being parked.
    /// </summary>
    final_chunk_processed,
}
