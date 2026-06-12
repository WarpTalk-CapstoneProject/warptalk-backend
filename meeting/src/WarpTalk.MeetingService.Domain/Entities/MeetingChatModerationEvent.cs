using System;

namespace WarpTalk.MeetingService.Domain.Entities;

public class MeetingChatModerationEvent
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid MeetingRoomId { get; set; }
    public Guid ModeratedByUserId { get; set; }
    public string Action { get; set; } = null!;
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }

    public virtual MeetingChatMessage Message { get; set; } = null!;
}
