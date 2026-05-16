using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioRoutingEventType
{
    participants_configured,
    session_started,
    translation_latency_high,
    translation_recovered,
    voice_quality_degraded,
    voice_recovered,
    audio_output_unavailable,
    audio_recovered,
    host_ended_session,
    routing_stopped_and_flushed,
    artifacts_finalized
}
