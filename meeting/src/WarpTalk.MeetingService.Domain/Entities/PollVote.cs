using System;

namespace WarpTalk.MeetingService.Domain.Entities;

public class PollVote
{
    public Guid Id { get; set; }

    public Guid PollId { get; set; }

    public Guid OptionId { get; set; }

    public Guid UserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
