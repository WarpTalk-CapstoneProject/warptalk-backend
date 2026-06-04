using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioRouteStatus
{
    PENDING,
    READY,
    BROADCASTING,
    PAUSED,
    SPEECH_DELAYED,
    TRANSLATION_DELAYED,
    VOICE_DELAYED,
    STANDARD_VOICE,
    CAPTION_ONLY,
    ENDING,
    SAVING_OUTPUTS,
    SAVE_FAILED,
    COMPLETED
}
