using System;

namespace WarpTalk.MeetingService.Domain.Entities;

public class Question
{
    public Guid Id { get; set; }

    public Guid MeetingRoomId { get; set; }

    public Guid AskedBy { get; set; }

    public string AskedByDisplayName { get; set; } = null!;

    public string Body { get; set; } = null!;

    // open | answered
    public string Status { get; set; } = "open";

    public DateTime CreatedAt { get; set; }

    public DateTime? AnsweredAt { get; set; }
}
