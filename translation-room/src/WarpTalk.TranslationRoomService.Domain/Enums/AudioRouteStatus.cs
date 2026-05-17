using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioRouteStatus
{
    IDLE,
    ROUTING_READY,
    AUDIO_ROUTING_ACTIVE,
    PAUSED,
    STOPPING,
    FINALIZING_ARTIFACTS,
    COMPLETED
}
