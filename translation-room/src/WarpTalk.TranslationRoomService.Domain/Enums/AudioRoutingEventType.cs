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

    tts_unavailable,
    audio_unavailable,
    audio_recovered,
    telemetry_state_updated,
}
