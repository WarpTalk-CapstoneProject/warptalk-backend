using System.Text.Json.Serialization;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.ValueObjects;

public class TranslationRoomSettings
{
    [JsonPropertyName("requires_approval")]
    public bool RequiresApproval { get; set; } = true;

    [JsonPropertyName("artifact_access")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ArtifactAccessLevel ArtifactAccess { get; set; } = ArtifactAccessLevel.HostOnly;
}
