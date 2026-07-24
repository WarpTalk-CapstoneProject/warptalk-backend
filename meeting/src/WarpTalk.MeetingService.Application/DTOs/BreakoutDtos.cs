using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Application.DTOs;

public class BreakoutGroupRequest
{
    public string? Label { get; set; }
    public List<Guid> UserIds { get; set; } = new();
}

public class CreateBreakoutsRequest
{
    public List<BreakoutGroupRequest> Groups { get; set; } = new();

    /// <summary>Optional countdown length. When set, the frontend computes "time remaining"
    /// from StartedAt + DurationSeconds itself (no server-side scheduled job — see
    /// BreakoutsService.EndBreakoutsAsync doc for why auto-expiry is not enforced server-side).</summary>
    public int? DurationSeconds { get; set; }
}

public class BreakoutSessionDto
{
    public Guid Id { get; set; }
    public string Label { get; set; } = null!;
    public string ProviderRoomName { get; set; } = null!;
    public List<Guid> UserIds { get; set; } = new();
}

public class CreateBreakoutsResponse
{
    public List<BreakoutSessionDto> Sessions { get; set; } = new();
    public int? DurationSeconds { get; set; }
    public DateTime StartedAt { get; set; }
}

/// <summary>Non-sensitive per-user routing info relayed to EVERYONE in the room via the hub
/// (Redis pub/sub broadcasts to the whole SignalR group, not to individual connections) — so
/// this intentionally carries no LiveKit token. Each client finds its own UserId in the list
/// and, if present, calls GET .../breakouts/my-assignment to mint its own token
/// (BreakoutJoinInfoDto below). Sending everyone's token to everyone in the group broadcast
/// would let any participant impersonate another user's breakout identity — this two-step
/// design avoids that.</summary>
public class BreakoutAssignmentRelayDto
{
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string Label { get; set; } = null!;
}

public class BreakoutJoinInfoDto
{
    public Guid SessionId { get; set; }
    public string Label { get; set; } = null!;
    public string ProviderRoomName { get; set; } = null!;
    public string Token { get; set; } = null!;
    public string ParticipantIdentity { get; set; } = null!;
    public int? DurationSeconds { get; set; }
    public DateTime? StartedAt { get; set; }
}
