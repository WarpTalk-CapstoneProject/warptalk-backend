using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationRoomType
{
    INSTANT,
    SCHEDULED,
    ONE_TO_ONE,
    GROUP,
    WEBINAR,
    B2B_VIRTUAL_MIC
}
