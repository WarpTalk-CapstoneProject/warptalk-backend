using System;

namespace WarpTalk.MeetingService.Domain.Entities;

public class PollOption
{
    public Guid Id { get; set; }

    public Guid PollId { get; set; }

    public string Label { get; set; } = null!;

    public int Position { get; set; }
}
