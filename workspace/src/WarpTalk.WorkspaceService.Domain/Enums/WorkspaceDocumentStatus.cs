using System.Text.Json.Serialization;

namespace WarpTalk.WorkspaceService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceDocumentStatus
{
    @public,
    pending_approval,
    rejected,
    archived
}
