namespace WarpTalk.MeetingService.Application.DTOs;

// WT-04: host controls.
public class LockRoomRequest
{
    public bool Locked { get; set; }
}

public class MuteOnEntryRequest
{
    public bool MuteOnEntry { get; set; }
}

// WT-06: recording via LiveKit Egress.
public class RecordingActionRequest
{
    /// <summary>"start" or "stop".</summary>
    public string Action { get; set; } = string.Empty;
}

public class RecordingStateDto
{
    public bool Recording { get; set; }
    public string? EgressId { get; set; }
}
