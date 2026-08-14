using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Authorization;

/// <summary>
/// "Who may START translation in this room" — host authority, or anyone already in the room when
/// the room has opted in via <c>participants_can_start_translation</c> (WT-371).
///
/// WHY IT IS ITS OWN FILE
///     This rule existed twice and only one copy was reachable. WT-371 implemented it as a private
///     <c>CanStartSessionAsync</c> on <c>TranslationRoomSessionService</c>, which serves
///     <c>POST /translation-rooms/{id}/sessions</c>. But the Start Translation button calls
///     <c>/resume</c>, and <c>TranslationRoomService.ResumeTranslationRoomAsync</c> gated on the raw
///     <c>room.IsHostedBy(userId)</c> instead — so the rule was enforced on an endpoint nothing
///     calls, and ignored on the one that runs.
///
///     The consequence was not a cosmetic mismatch. /resume is the only path that opens a
///     <c>TranslationRoomSession</c>, and that row is the whole of
///     <c>translation_active</c> in <c>AudioRouteCacheService.PublishRoutesUpdateAsync</c>, which
///     the AI translation worker gates every STT result on. A participant in an opted-in room saw
///     the button (the web control bar reads the WT-371 rule), pressed it, got 401 from a branch
///     that returns without logging, and the meeting produced no dubbed audio at all with nothing
///     anywhere saying why. That is WT-373.
///
/// STOPPING IS DELIBERATELY NOT HERE
///     <c>StopTranslationAsync</c> and <c>PauseTranslationRoomAsync</c> stay host-only. Letting a
///     room decide who may start translation is not the same as letting anyone cut it off for
///     everybody, and WT-371 only ever opened the starting half.
/// </summary>
public static class RoomStartTranslationAccess
{
    public static async Task<bool> CanStartTranslationAsync(
        TranslationRoom room,
        Guid requestedByUserId,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        ITranslationRoomParticipantRepository participantRepository,
        CancellationToken ct = default)
    {
        // Host identity first, for the reason RoomHostAccess documents: the host path must not
        // depend on WorkspaceService being reachable, and must not cost a gRPC hop per press.
        if (await RoomHostAccess.HasHostAuthorityAsync(room, requestedByUserId, workspaceMemberDirectory, ct))
        {
            return true;
        }

        if (!TranslationRoomMapper.ReadSettings(room.Settings).ParticipantsCanStartTranslation)
        {
            return false;
        }

        // Opted in, but only for people actually in the room. Without this clause the setting
        // would let any authenticated stranger who knows the room id start billable AI in it.
        return await participantRepository.AnyAsync(
            participant => participant.TranslationRoomId == room.Id
                && participant.UserId == requestedByUserId,
            ct);
    }
}
