using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

/// <summary>
/// WT-06: starts/stops a LiveKit RoomComposite Egress (recording) for a room. There is no
/// LiveKit server SDK dependency in this codebase — LiveKitTokenService hand-rolls its JWTs
/// with plain JwtSecurityTokenHandler rather than a LiveKit NuGet package — so this follows
/// the same convention: a thin, hand-rolled HTTP client against LiveKit's Twirp-based Egress
/// API, instead of introducing a brand-new external dependency.
/// </summary>
public interface ILiveKitEgressService
{
    Task<Result<string>> StartRoomCompositeEgressAsync(string roomName, CancellationToken ct = default);
    Task<Result<bool>> StopEgressAsync(string egressId, CancellationToken ct = default);
}
