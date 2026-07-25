using System;

namespace WarpTalk.MeetingService.Domain.Entities;

// WT-Breakout: a scoped-down breakout room — one LiveKit provider room "child" of the
// parent MeetingRoom. See BreakoutsService for the create/end flow.
public class BreakoutSession
{
    public Guid Id { get; set; }

    public Guid ParentMeetingRoomId { get; set; }

    public string ProviderRoomName { get; set; } = null!;

    public string Label { get; set; } = null!;

    public int? DurationSeconds { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
