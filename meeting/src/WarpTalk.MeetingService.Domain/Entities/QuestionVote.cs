using System;

namespace WarpTalk.MeetingService.Domain.Entities;

public class QuestionVote
{
    public Guid Id { get; set; }

    public Guid QuestionId { get; set; }

    public Guid UserId { get; set; }
}
