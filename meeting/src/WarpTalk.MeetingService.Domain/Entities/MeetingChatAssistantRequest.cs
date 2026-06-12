using System;

namespace WarpTalk.MeetingService.Domain.Entities;

public class MeetingChatAssistantRequest
{
    public Guid Id { get; set; }
    public Guid TriggerMessageId { get; set; }
    public Guid MeetingRoomId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string Prompt { get; set; } = null!;
    public string ContextScope { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public virtual MeetingChatMessage TriggerMessage { get; set; } = null!;
}
