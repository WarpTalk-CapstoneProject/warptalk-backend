using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Application.DTOs;

public class PollOptionDto
{
    public Guid Id { get; set; }
    public string Label { get; set; } = null!;
    public int Position { get; set; }
    public int VoteCount { get; set; }
}

public class PollDto
{
    public Guid Id { get; set; }
    public Guid CreatedBy { get; set; }
    public string Question { get; set; } = null!;
    public bool IsMultipleChoice { get; set; }
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public List<PollOptionDto> Options { get; set; } = new();

    /// <summary>Option ids the CALLER has voted for — resolved per-viewer, not persisted on the DTO.</summary>
    public List<Guid> MyVotedOptionIds { get; set; } = new();
}

public class CreatePollRequest
{
    public string Question { get; set; } = null!;
    public List<string> Options { get; set; } = new();
    public bool IsMultipleChoice { get; set; }
}

public class VotePollRequest
{
    public List<Guid> OptionIds { get; set; } = new();
}
