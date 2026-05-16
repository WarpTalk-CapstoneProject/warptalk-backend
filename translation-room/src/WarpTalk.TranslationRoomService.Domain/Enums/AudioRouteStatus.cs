using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioRouteStatus
{
    IDLE,
    ROUTING_READY,
    AUDIO_ROUTING_ACTIVE,
    TRANSLATION_DEGRADED,
    VOICE_QUALITY_DEGRADED,
    TEXT_ONLY_MODE,
    STOPPING,
    FINALIZING_ARTIFACTS,
    COMPLETED
}
