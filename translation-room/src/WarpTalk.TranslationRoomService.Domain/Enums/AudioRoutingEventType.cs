using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioRoutingEventType
{
    // Modern diagram-aligned events
    config_ready,
    session_starts,
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
