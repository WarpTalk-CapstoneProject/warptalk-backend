using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Authorization;

/// <summary>
/// "Who may act as the host of this room" — the write-side counterpart of
/// <see cref="Domain.Authorization.RoomReadAccess"/>.
///
/// This rule (room host OR workspace Owner/Admin) was established by WT-188 and reconciled by
/// WT-313, but it lived as a private method on <c>TranslationRoomParticipantService</c>, so the
/// next service that needed it had no way to reuse it. It sits in Application rather than beside
/// RoomReadAccess in Domain because the Owner/Admin half is not a clause the translation-room
/// database can answer: it is a gRPC call into WorkspaceService, which is also why RoomReadAccess
/// deliberately does not model it.
///
/// Order matters and is part of the contract: host identity is checked first, so the host path
/// never depends on WorkspaceService being reachable and callers on a polling loop do not make a
/// gRPC hop per request. <c>AdmitParticipantAsync_ShouldAdmit_WhenRequesterIsRoomHost</c> pins
/// this by asserting <see cref="IWorkspaceMemberDirectory.IsOwnerOrAdminAsync"/> is never called
/// for a host.
/// </summary>
public static class RoomHostAccess
{
    public static async Task<bool> HasHostAuthorityAsync(
        TranslationRoom room,
        Guid requestedByUserId,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        CancellationToken ct = default)
    {
        // WT-359: effective host — the transferee once a handover has happened, the booker otherwise.
        if (room.IsHostedBy(requestedByUserId))
            return true;

        return await workspaceMemberDirectory.IsOwnerOrAdminAsync(room.WorkspaceId, requestedByUserId, ct);
    }
}
