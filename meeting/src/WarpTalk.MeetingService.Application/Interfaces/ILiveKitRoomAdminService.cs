using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface ILiveKitRoomAdminService
{
    Task<Result<bool>> RemoveParticipantAsync(
        string roomName,
        string participantIdentity,
        CancellationToken ct = default);

    Task<Result<bool>> DeleteRoomAsync(
        string roomName,
        CancellationToken ct = default);
}
