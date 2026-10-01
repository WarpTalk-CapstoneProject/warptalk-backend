namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The little this service needs to know about a translation room to serve its Google Meet side:
/// whether it is a bridge, which Meet it bridges, who hosts it and who is in it.
/// </summary>
/// <param name="EffectiveHostId">The host after any transfer — whose Google grant is used.</param>
/// <param name="BookerId">The room's original host, kept for the "host" access rule.</param>
/// <param name="MemberUserIds">Participants who were admitted (not kicked, not rejected).</param>
/// <param name="WorkspaceId">The room's workspace — where the host's google_meet plugin is judged.</param>
public sealed record BridgeRoomInfo(
    Guid RoomId,
    bool IsBridge,
    string? ExternalMeetingUrl,
    Guid? EffectiveHostId,
    Guid? BookerId,
    IReadOnlyCollection<Guid> MemberUserIds,
    Guid? WorkspaceId = null);

public enum BridgeRoomLookupStatus
{
    Found,
    NotFound,
    Unavailable,
}

public interface IBridgeRoomDirectory
{
    Task<(BridgeRoomLookupStatus Status, BridgeRoomInfo? Room)> GetAsync(Guid roomId, CancellationToken ct = default);
}
