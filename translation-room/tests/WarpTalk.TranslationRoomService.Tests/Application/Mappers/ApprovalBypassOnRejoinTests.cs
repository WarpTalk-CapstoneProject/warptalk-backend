using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Mappers;

/// <summary>
/// WT-563: a participant let themselves into a requires-approval room by refreshing.
///
/// Two reasonable decisions collided.
///
/// TranslationRoomParticipantService turns a WAITING row into LEFT when the tab closes, so a
/// closed lobby tab clears the queue instead of leaving a knock nobody can act on. And this
/// mapper treated LEFT as proof of admission, so a reconnect would not send an admitted
/// participant back through the lobby.
///
/// Each is defensible alone. Together: knock (WAITING) → refresh, which writes LEFT → rejoin,
/// which read LEFT as "already admitted" and set CONNECTED. The host was never asked. Reported
/// from production against a room with approval switched on.
///
/// Fixed at the source rather than in the mapper. Somebody who abandons the lobby now returns to
/// INVITED — known to the room, not yet admitted — which clears the queue exactly as LEFT did
/// while leaving the next join to re-evaluate approval. The paths that mark a room's occupants
/// DISCONNECTED no longer touch the lobby either.
///
/// That restores the invariant the mapper's shortcut has always assumed and never enforced: a
/// participant who has never been admitted can only be INVITED or WAITING. Which is also what
/// lets somebody who genuinely LEFT a meeting walk back in without queueing twice — a real
/// requirement, and the reason the obvious fix (re-queue anyone who is LEFT) is the wrong one.
/// </summary>
public class ApprovalBypassOnRejoinTests
{
    private static JoinTranslationRoomRequest Request() =>
        new("ROOMCODE", "Someone", "en", "vi");

    private static TranslationRoomParticipant ParticipantWith(string status) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Someone",
        Role = nameof(TranslationRoomParticipantRole.PARTICIPANT),
        Status = status,
        ListenLanguage = "vi",
        SpeakLanguage = "en",
    };

    // ── the bypass ───────────────────────────────────────────────────────────

    [Fact]
    public void SomebodyReturnedToInvitedGoesBackToTheLobby()
    {
        // The reported bypass, after the source fix: the refresh now writes INVITED rather than
        // LEFT, so the rejoin re-evaluates approval instead of reading admission into it.
        var participant = ParticipantWith(TranslationRoomParticipantStatuses.Invited);

        participant.UpdateFrom(Request(), "en", "vi", requiresApproval: true, isHost: false);

        Assert.Equal(TranslationRoomParticipantStatuses.Waiting, participant.Status);
    }

    [Fact]
    public void AnInvitedArrivalStillWaits()
    {
        var participant = ParticipantWith(TranslationRoomParticipantStatuses.Invited);

        participant.UpdateFrom(Request(), "en", "vi", requiresApproval: true, isHost: false);

        Assert.Equal(TranslationRoomParticipantStatuses.Waiting, participant.Status);
    }

    // ── what must keep working ───────────────────────────────────────────────

    [Fact]
    public void AnAdmittedParticipantWhoDroppedTheirConnectionIsNotRequeued()
    {
        // DISCONNECTED is only ever written for a participant who was CONNECTED, so it is proof
        // the host already said yes. Losing wifi must not put them back in the queue.
        var participant = ParticipantWith(TranslationRoomParticipantStatuses.Disconnected);

        participant.UpdateFrom(Request(), "en", "vi", requiresApproval: true, isHost: false);

        Assert.Equal(TranslationRoomParticipantStatuses.Connected, participant.Status);
        Assert.Null(participant.LeftAt);
    }

    [Fact]
    public void SomebodyWhoGenuinelyLeftTheMeetingIsNotRequeued()
    {
        // The requirement that rules out the obvious fix. LEFT now only ever describes a
        // participant the host DID admit, so it still means what the shortcut assumes.
        var participant = ParticipantWith(TranslationRoomParticipantStatuses.Left);
        participant.LeftAt = DateTime.UtcNow;

        participant.UpdateFrom(Request(), "en", "vi", requiresApproval: true, isHost: false);

        Assert.Equal(TranslationRoomParticipantStatuses.Connected, participant.Status);
        Assert.Null(participant.LeftAt);
    }

    [Fact]
    public void WithoutApprovalAnArrivalGoesStraightIn()
    {
        // A room that asks for no approval must not gain a lobby from this change.
        var participant = ParticipantWith(TranslationRoomParticipantStatuses.Invited);

        participant.UpdateFrom(Request(), "en", "vi", requiresApproval: false, isHost: false);

        Assert.Equal(TranslationRoomParticipantStatuses.Connected, participant.Status);
    }

    [Fact]
    public void TheHostNeverWaitsInTheirOwnLobby()
    {
        var participant = ParticipantWith(TranslationRoomParticipantStatuses.Invited);

        participant.UpdateFrom(Request(), "en", "vi", requiresApproval: true, isHost: true);

        Assert.Equal(TranslationRoomParticipantStatuses.Connected, participant.Status);
        Assert.Equal(nameof(TranslationRoomParticipantRole.HOST), participant.Role);
    }

    [Fact]
    public void AKickedParticipantIsNotSilentlyReadmitted()
    {
        // Not this mapper's gate — the join refuses KICKED before reaching here — but nothing in
        // these branches may quietly promote them either.
        var participant = ParticipantWith(TranslationRoomParticipantStatuses.Kicked);

        participant.UpdateFrom(Request(), "en", "vi", requiresApproval: true, isHost: false);

        Assert.Equal(TranslationRoomParticipantStatuses.Kicked, participant.Status);
    }
}
