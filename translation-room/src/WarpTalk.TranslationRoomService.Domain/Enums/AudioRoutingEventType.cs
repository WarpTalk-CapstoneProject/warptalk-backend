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
    translation_latency_high,
    translation_recovered,
    tts_latency_high,
    tts_recovered,

    voice_clone_unavailable,
    voice_clone_recovered,

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
}
