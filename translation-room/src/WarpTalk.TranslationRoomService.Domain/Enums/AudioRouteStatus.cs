using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioRouteStatus
{
    IDLE,
    ROUTING_READY,
    AUDIO_ROUTING_ACTIVE,
    AUDIO_ROUTING_PAUSED,
    STT_DEGRADED,
    TRANSLATION_DEGRADED,
    TTS_DEGRADED,
    VOICE_CLONE_FALLBACK,
    TEXT_ONLY_MODE,
    STOPPING,
    FINALIZING_ARTIFACTS,
    FINALIZING_ARTIFACTS_FAILED,
    COMPLETED
}
