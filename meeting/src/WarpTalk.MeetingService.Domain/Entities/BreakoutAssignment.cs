using System;

namespace WarpTalk.MeetingService.Domain.Entities;

public class BreakoutAssignment
{
    public Guid Id { get; set; }

    public Guid BreakoutSessionId { get; set; }

    public Guid UserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
