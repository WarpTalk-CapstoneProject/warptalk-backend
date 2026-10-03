namespace WarpTalk.MeetingService.Application.DTOs;

/// <summary>
/// A participant left a LiveKit room whose recording was running: ask whether the Google Meet
/// bridge session behind it has ended. See MeetingRoomService.StopRecordingIfBridgeEndedAsync.
/// </summary>
/// <param name="ProviderRoomName">The LiveKit room the participant left.</param>
/// <param name="EgressId">The egress that was running when they left — only THAT egress is ever stopped.</param>
/// <param name="DepartedAtUtc">When the departure was received; the grace period is measured from it.</param>
public sealed record BridgeRecordingEndRequest(string ProviderRoomName, string EgressId, DateTime DepartedAtUtc);

public enum BridgeRecordingEndOutcome
{
    /// <summary>The recording was stopped, through the same path as the Stop button.</summary>
    Stopped,

    /// <summary>Nothing to do: not a bridge room, a different/no recording, or it is already ending.</summary>
    Ignored,

    /// <summary>Somebody is still in the room, so the bridge session has not ended.</summary>
    Occupied,

    /// <summary>The room is empty but the grace period has not run out: ask again after <c>RetryAfter</c>.</summary>
    AwaitingGrace,

    /// <summary>A lookup or the stop itself failed. LiveKit's own empty-room close still ends the egress.</summary>
    Failed,
}

/// <summary>The answer to a <see cref="BridgeRecordingEndRequest"/>.</summary>
public sealed record BridgeRecordingEndCheck(BridgeRecordingEndOutcome Outcome, string Reason, TimeSpan? RetryAfter = null)
{
    public static BridgeRecordingEndCheck Ignored(string reason) => new(BridgeRecordingEndOutcome.Ignored, reason);
}
