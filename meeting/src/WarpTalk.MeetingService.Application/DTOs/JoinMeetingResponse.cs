namespace WarpTalk.MeetingService.Application.DTOs;

public class JoinMeetingResponse
{
    public string Token { get; set; } = string.Empty;
    public string ProviderRoomName { get; set; } = string.Empty;
    public string ParticipantIdentity { get; set; } = string.Empty;
}
