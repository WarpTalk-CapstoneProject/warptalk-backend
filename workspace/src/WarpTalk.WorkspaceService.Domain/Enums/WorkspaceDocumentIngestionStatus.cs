using System.Text.Json.Serialization;

namespace WarpTalk.WorkspaceService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceDocumentIngestionStatus
{
    awaiting_approval,
    pending,
    processing,
    completed,
    failed,
    skipped
}
