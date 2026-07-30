using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationRoomParticipantRole
{
    PARTICIPANT,
    HOST
}
