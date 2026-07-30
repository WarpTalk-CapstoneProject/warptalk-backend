using System.Text.Json.Serialization;

namespace WarpTalk.TranslationRoomService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactAccessLevel
{
    HostOnly,
    Participants,
    Workspace
}
