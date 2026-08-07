using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// "Is this caller allowed to act as the host of this room?" — for the SignalR hub.
///
/// TranslationRoomHub's host-only methods (MuteAll, SpotlightParticipant, AdmitWaitingParticipant)
/// carried a self-documented KNOWN GAP: they trusted the caller's claimed identity from the JWT and
/// verified nothing, because the hub "has no injected repository or gRPC client for
/// TranslationRoom/host data". That was true of the hub, but not of the Gateway — Program.cs has
/// registered both <c>TranslationRoomServiceClient</c> and <c>WorkspaceServiceClient</c> for some
/// time, and <c>GetTranslationRoomById</c> already returns <c>hostId</c>. This is the "gRPC client
/// to TranslationRoomService injected into this hub" that the gap comment asked for.
/// </summary>
public interface IRoomHostAuthority
{
    Task<bool> HasHostAuthorityAsync(Guid translationRoomId, string userId, CancellationToken ct = default);
}

/// <summary>
/// The same predicate the REST side enforces, spelled once here rather than a fourth time.
///
/// TranslationRoomParticipantService.HasRoomHostAuthorityAsync is "room host OR workspace
/// Owner/Admin" — deliberately not host-only, because WT-188 established that the web client grants
/// host-like room controls to workspace Owners/Admins and restricting these actions to
/// <c>room.HostId</c> would 403 exactly the people the UI shows the buttons to. Reproducing only
/// the host clause here would re-create that bug in the hub, so both clauses are checked, in the
/// same order and with the same failure semantics:
///
///  - Host identity first, so the host path never depends on WorkspaceService being reachable.
///  - Owner/Admin second, and a WorkspaceService failure only ever fails to WIDEN — it cannot turn
///    a legitimate host's action into an error.
///
/// Fails CLOSED on the room lookup: if TranslationRoomService cannot tell us who the host is, we do
/// not know the caller is not an impostor, so the action is refused. That is the whole point of the
/// change — a soft-failing check is the gap it replaces.
///
/// No caching. These are rare, human-initiated host actions (mute-all, spotlight, approve), so one
/// gRPC hop each is cheap; a cached hostId would also keep answering "yes" for the previous host
/// for the length of its TTL after a host transfer, which is the exact hijack this closes.
/// </summary>
public sealed class RoomHostAuthority : IRoomHostAuthority
{
    private const string OwnerRole = "Owner";
    private const string AdminRole = "Admin";

    private readonly Shared.Protos.TranslationRoomService.TranslationRoomServiceClient _roomClient;
    private readonly WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly ILogger<RoomHostAuthority> _logger;

    public RoomHostAuthority(
        Shared.Protos.TranslationRoomService.TranslationRoomServiceClient roomClient,
        WorkspaceService.WorkspaceServiceClient workspaceClient,
        ILogger<RoomHostAuthority> logger)
    {
        _roomClient = roomClient;
        _workspaceClient = workspaceClient;
        _logger = logger;
    }

    public async Task<bool> HasHostAuthorityAsync(Guid translationRoomId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        GetTranslationRoomResponse room;
        try
        {
            room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = translationRoomId.ToString() },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Fail closed, unlike the workspace lookup below: without the room we have no host to
            // compare against, so allowing the action would be exactly the unverified trust this
            // type exists to remove.
            _logger.LogWarning(
                ex,
                "RoomHostAuthority: could not resolve room {RoomId} to authorize {UserId}; refusing the host action.",
                translationRoomId,
                userId);
            return false;
        }

        if (string.Equals(room.HostId, userId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Guid.TryParse(room.WorkspaceId, out var workspaceId) || !Guid.TryParse(userId, out _))
        {
            return false;
        }

        try
        {
            var member = await _workspaceClient.GetWorkspaceMemberDetailsAsync(
                new GetWorkspaceMemberRequest
                {
                    WorkspaceId = workspaceId.ToString(),
                    UserId = userId
                },
                cancellationToken: ct);

            if (!member.IsMember || !member.IsActive)
            {
                return false;
            }

            return string.Equals(member.RoleName, OwnerRole, StringComparison.OrdinalIgnoreCase)
                || string.Equals(member.RoleName, AdminRole, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Same reasoning as WorkspaceMemberGrpcDirectory: this branch can only widen a decision
            // already denied on host identity, so a WorkspaceService outage must not become a 500
            // for a legitimate non-host caller.
            _logger.LogWarning(
                ex,
                "RoomHostAuthority: failed to resolve workspace membership. WorkspaceId: {WorkspaceId}, UserId: {UserId}",
                workspaceId,
                userId);
            return false;
        }
    }
}
