using System.Text.Json.Serialization;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.ValueObjects;

public class TranslationRoomSettings
{
    [JsonPropertyName("requires_approval")]
    public bool RequiresApproval { get; set; } = true;

    [JsonPropertyName("artifact_access")]
    public string ArtifactAccess { get; set; } = "HOST_ONLY";
}
