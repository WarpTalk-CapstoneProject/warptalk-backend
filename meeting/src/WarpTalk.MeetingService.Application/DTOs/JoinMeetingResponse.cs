namespace WarpTalk.MeetingService.Application.DTOs;

public class JoinMeetingResponse
{
    public string Token { get; set; } = string.Empty;
    public string ProviderRoomName { get; set; } = string.Empty;
    public string ParticipantIdentity { get; set; } = string.Empty;
    public string? ActiveHostId { get; set; }
    public bool IsWaitingRoom { get; set; } = false;

    /// <summary>WT-04: the room's mute-on-entry setting — the frontend defaults the local
    /// mic to muted on first mount when this is true.</summary>
    public bool MuteOnEntry { get; set; } = false;
}
