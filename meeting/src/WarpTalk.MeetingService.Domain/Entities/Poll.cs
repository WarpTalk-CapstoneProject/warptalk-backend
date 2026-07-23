using System;

namespace WarpTalk.MeetingService.Domain.Entities;

public class Poll
{
    public Guid Id { get; set; }

    public Guid MeetingRoomId { get; set; }

    public Guid CreatedBy { get; set; }

    public string Question { get; set; } = null!;

    public bool IsMultipleChoice { get; set; }

    // open | closed
    public string Status { get; set; } = "open";

    public DateTime CreatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}
