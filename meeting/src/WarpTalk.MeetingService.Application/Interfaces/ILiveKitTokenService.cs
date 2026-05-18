using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface ILiveKitTokenService
{
    Result<string> GenerateToken(string roomName, string participantIdentity, string participantName, bool canPublish, bool canSubscribe);
}
