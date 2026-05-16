using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationRoomParticipantStatus
{
    INVITED,
    WAITING,
    CONNECTED,
    DISCONNECTED,
    LEFT,
    KICKED,
    REJECTED
}
