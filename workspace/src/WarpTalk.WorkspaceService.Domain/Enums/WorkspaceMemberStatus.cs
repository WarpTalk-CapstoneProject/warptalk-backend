using System.Text.Json.Serialization;

namespace WarpTalk.WorkspaceService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceMemberStatus
{
    Active,
    Suspended,
    Removed
}
