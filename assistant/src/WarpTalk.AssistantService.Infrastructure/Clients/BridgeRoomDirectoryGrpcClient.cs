using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AssistantService.Infrastructure.Clients;

/// <summary>Reads a room and its roster from TranslationRoomService's existing gRPC reads.</summary>
public sealed class BridgeRoomDirectoryGrpcClient : IBridgeRoomDirectory
{
    // Removed from the room by the host: no longer someone the room's data is shown to.
    private static readonly HashSet<string> ExcludedStatuses = new(StringComparer.OrdinalIgnoreCase) { "KICKED", "REJECTED" };

    private readonly TranslationRoomService.TranslationRoomServiceClient _client;
    private readonly ILogger<BridgeRoomDirectoryGrpcClient> _logger;

    public BridgeRoomDirectoryGrpcClient(
        TranslationRoomService.TranslationRoomServiceClient client,
        ILogger<BridgeRoomDirectoryGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<(BridgeRoomLookupStatus Status, BridgeRoomInfo? Room)> GetAsync(Guid roomId, CancellationToken ct = default)
    {
        try
        {
            var room = await _client.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = roomId.ToString() },
                cancellationToken: ct);
            var participants = await _client.GetParticipantsByRoomIdAsync(
                new GetParticipantsByRoomIdRequest { RoomId = roomId.ToString() },
                cancellationToken: ct);

            var members = participants.Participants
                .Where(p => !ExcludedStatuses.Contains(p.Status ?? string.Empty))
                .Select(p => Guid.TryParse(p.Id, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToHashSet();

            var booker = Guid.TryParse(room.HostId, out var hostId) ? hostId : (Guid?)null;
            var effective = Guid.TryParse(room.EffectiveHostId, out var effectiveId) ? effectiveId : booker;

            return (BridgeRoomLookupStatus.Found, new BridgeRoomInfo(
                roomId,
                ExternalBridgeConstants.IsBridgeRoomType(room.TranslationRoomType),
                string.IsNullOrWhiteSpace(room.ExternalMeetingUrl) ? null : room.ExternalMeetingUrl,
                effective,
                booker,
                members,
                Guid.TryParse(room.WorkspaceId, out var workspaceId) ? workspaceId : null));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return (BridgeRoomLookupStatus.NotFound, null);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Room lookup for {RoomId} failed.", roomId);
            return (BridgeRoomLookupStatus.Unavailable, null);
        }
    }
}
