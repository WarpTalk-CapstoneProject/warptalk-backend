using System;

namespace WarpTalk.MeetingService.Application.DTOs;

public class QuestionDto
{
    public Guid Id { get; set; }
    public Guid AskedBy { get; set; }
    public string AskedByDisplayName { get; set; } = null!;
    public string Body { get; set; } = null!;
    public string Status { get; set; } = null!;
    public int UpvoteCount { get; set; }
    public bool UpvotedByMe { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
}

public class CreateQuestionRequest
{
    public string Body { get; set; } = null!;
    public string? DisplayName { get; set; }
}
