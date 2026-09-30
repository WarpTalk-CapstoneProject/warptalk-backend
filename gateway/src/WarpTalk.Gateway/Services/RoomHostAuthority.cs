using WarpTalk.Shared;
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

    /// <summary>
    /// WT-699 / TC1806: may this caller's connection join the room's broadcast group, sit in its
    /// lobby group, or neither? Asked by TranslationRoomHub.JoinTranslationRoom, which used to add
    /// ANY authenticated connection to <c>translationRoom:{id}</c> — a stranger, a guest still
    /// waiting for approval, a kicked participant — and hand it every transcript line, translation,
    /// chat message and roster change the room broadcast.
    ///
    /// Lives on this interface rather than a new one so the hub's constructor does not change: it
    /// is the same gRPC dependency answering the same kind of question about the same room.
    /// </summary>
    Task<RoomAdmission> GetRoomAdmissionAsync(Guid translationRoomId, string userId, CancellationToken ct = default);

    /// <summary>
    /// May this caller say what the far side of this room's external call speaks — that is, move
    /// the "External Meeting" stand-in's language? Asked by
    /// TranslationRoomHub.SetExternalMeetingLanguage.
    ///
    /// Deliberately NARROWER than <see cref="HasHostAuthorityAsync"/>: the same two gates as the
    /// bridge-token endpoint (MeetingRoomService.GenerateBridgeTokenAsync), because it acts on the
    /// same identity. Only an EXTERNAL_BRIDGE room has a stand-in, and only its host — the one
    /// person whose device publishes the far side's audio — speaks for it. A workspace Owner/Admin
    /// who is not in the call has no way to know what the far side is speaking. An ended room is
    /// refused: there is no mesh left to re-route.
    /// </summary>
    Task<bool> CanSetExternalMeetingLanguageAsync(Guid translationRoomId, string userId, CancellationToken ct = default);
}

/// <summary>WT-699 / TC1806: where a connection may sit relative to a room's broadcasts.</summary>
public enum RoomAdmission
{
    /// <summary>No broadcasts at all: not on the roster, left, kicked, rejected — or unverifiable.</summary>
    Refused,

    /// <summary>
    /// Knocking (WAITING, or INVITED after a dropped lobby tab). Gets only the lobby group, which
    /// carries the answer to the knock and nothing said inside the meeting.
    /// </summary>
    Lobby,

    /// <summary>The effective host, or a participant the room has admitted (CONNECTED/DISCONNECTED).</summary>
    Admitted,
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

    /// <inheritdoc />
    /// <remarks>Fails closed on the room lookup, as the host check does.</remarks>
    public async Task<bool> CanSetExternalMeetingLanguageAsync(Guid translationRoomId, string userId, CancellationToken ct = default)
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
            _logger.LogWarning(
                ex,
                "RoomHostAuthority: could not resolve room {RoomId} to authorize {UserId} to set the external meeting's language; refusing.",
                translationRoomId,
                userId);
            return false;
        }

        return IsExternalBridgeHost(room, userId);
    }

    /// <summary>
    /// The pure half of <see cref="CanSetExternalMeetingLanguageAsync"/>. An empty room type (a
    /// response from a server older than the field) is "not a bridge", never a permissive default —
    /// the same reading the proto comment on translation_room_type requires.
    /// </summary>
    public static bool IsExternalBridgeHost(GetTranslationRoomResponse room, string userId)
    {
        if (!ExternalBridgeConstants.IsBridgeRoomType(room.TranslationRoomType))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(room.HostId)
            || !string.Equals(room.HostId, userId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var status = room.Status?.Trim().ToUpperInvariant();
        return status is not ("ENDED" or "FINISHED" or "CANCELLED" or "EXPIRED");
    }

    /// <inheritdoc />
    /// <remarks>
    /// DISCONNECTED counts as admitted on purpose: SignalR's automatic reconnect re-enters through
    /// JoinTranslationRoom after the dropped socket has already been recorded as DISCONNECTED
    /// (MarkParticipantDisconnectedAsync), and refusing it would strand everybody whose wifi
    /// blinked. It is the one status that proves the person already held a seat.
    ///
    /// Fails CLOSED, like the host check: if the roster cannot be read we cannot tell a participant
    /// from a stranger, and the whole point is not to broadcast a meeting to a stranger. The web
    /// client retries its join, so a transient failure costs a moment, not the meeting.
    /// </remarks>
    public async Task<RoomAdmission> GetRoomAdmissionAsync(Guid translationRoomId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return RoomAdmission.Refused;
        }

        GetParticipantsByRoomIdResponse roster;
        try
        {
            roster = await _roomClient.GetParticipantsByRoomIdAsync(
                new GetParticipantsByRoomIdRequest { RoomId = translationRoomId.ToString() },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RoomHostAuthority: could not read the roster of room {RoomId} to admit {UserId}; refusing the join.",
                translationRoomId,
                userId);
            return RoomAdmission.Refused;
        }

        var row = roster.Participants.FirstOrDefault(
            p => string.Equals(p.Id, userId, StringComparison.OrdinalIgnoreCase));

        if (row is not null)
        {
            switch (NormalizedStatus(row))
            {
                case "CONNECTED":
                case "DISCONNECTED":
                    return RoomAdmission.Admitted;
                case "WAITING":
                case "INVITED":
                    return RoomAdmission.Lobby;
                case "KICKED":
                case "REJECTED":
                    // Terminal, and the host cannot be kicked, so there is nothing to fall back on.
                    return RoomAdmission.Refused;
            }
        }

        // No row, or LEFT: the only person still entitled to the room is its host — who is never
        // turned away from their own meeting, even ahead of their REST join landing.
        return await HasEffectiveHostIdentityAsync(translationRoomId, userId, ct)
            ? RoomAdmission.Admitted
            : RoomAdmission.Refused;
    }

    /// <summary>
    /// The stored status, or — from a room service that predates the field — the only thing it
    /// did send: IsActive means CONNECTED, anything else is treated as a knock. That fallback can
    /// only ever put somebody in the LOBBY group, which carries nothing said inside the meeting.
    /// </summary>
    private static string NormalizedStatus(Participant row)
    {
        if (!string.IsNullOrWhiteSpace(row.Status))
        {
            return row.Status.Trim().ToUpperInvariant();
        }

        return row.IsActive ? "CONNECTED" : "WAITING";
    }

    private async Task<bool> HasEffectiveHostIdentityAsync(Guid translationRoomId, string userId, CancellationToken ct)
    {
        try
        {
            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = translationRoomId.ToString() },
                cancellationToken: ct);

            var effectiveHost = string.IsNullOrEmpty(room.EffectiveHostId) ? room.HostId : room.EffectiveHostId;
            return string.Equals(effectiveHost, userId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RoomHostAuthority: could not resolve the host of room {RoomId} for {UserId}; refusing the join.",
                translationRoomId,
                userId);
            return false;
        }
    }
}
