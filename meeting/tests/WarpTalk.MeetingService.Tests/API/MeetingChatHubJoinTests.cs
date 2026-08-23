using Microsoft.AspNetCore.SignalR;
using Moq;
using System.Linq.Expressions;
using System.Security.Claims;
using WarpTalk.MeetingService.API.Hubs;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Tests.API;

/// <summary>
/// Joining a meeting's chat group.
///
/// Two defects lived in this one method, and they pulled in opposite directions — which is why
/// neither was obvious. It REJECTED callers who belonged there, because the meeting room row is
/// provisioned by MeetingRoomService.JoinMeetingAsync and the chat hub connects alongside that
/// call rather than after it; and it ADMITTED callers who did not, by creating the participant
/// row itself with no authorization of any kind.
///
/// The second is the serious one. MeetingChatService gates reading and sending on
/// `room.CreatedBy == userId || participant != null`, so the hub was minting exactly the record
/// those gates look for: any authenticated user who knew a translation room id could call this,
/// become a participant of a meeting nobody invited them to, and then read its history over REST.
/// </summary>
public sealed class MeetingChatHubJoinTests
{
    private static readonly Guid TranslationRoomId = Guid.NewGuid();
    private static readonly Guid MeetingRoomId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IMeetingRoomRepository> _rooms = new();
    private readonly Mock<IMeetingParticipantRepository> _participants = new();
    private readonly Mock<IGroupManager> _groups = new();

    private MeetingChatHub CreateHub(bool authenticated = true)
    {
        _unitOfWork.Setup(u => u.MeetingRoomRepository).Returns(_rooms.Object);
        _unitOfWork.Setup(u => u.MeetingParticipantRepository).Returns(_participants.Object);

        var identity = authenticated
            ? new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "test")
            : new ClaimsIdentity();

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns("connection-1");
        context.Setup(c => c.User).Returns(new ClaimsPrincipal(identity));

        return new MeetingChatHub(_unitOfWork.Object)
        {
            Context = context.Object,
            Groups = _groups.Object,
        };
    }

    private void RoomExists(Guid? createdBy = null) =>
        _rooms.Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeetingRoom
            {
                Id = MeetingRoomId,
                TranslationRoomId = TranslationRoomId,
                CreatedBy = createdBy,
            });

    private void RoomMissing() =>
        _rooms.Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingRoom?)null);

    private void ParticipantExists(bool exists) =>
        _participants.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(exists
                ? new MeetingParticipant { MeetingRoomId = MeetingRoomId, UserId = UserId }
                : null);

    // ── it must not manufacture the membership that authorizes ──────────────

    [Fact]
    public async Task AStrangerIsRefused_AndNoParticipantRowIsCreated()
    {
        RoomExists();
        ParticipantExists(false);
        var hub = CreateHub();

        var error = await Assert.ThrowsAsync<HubException>(() => hub.JoinMeetingRoom(TranslationRoomId));

        Assert.Equal(MeetingChatHub.NotAParticipant, error.Message);
        // The heart of it: creating this row is what let a stranger pass MeetingChatService's
        // own authorization afterwards.
        _participants.Verify(p => p.AddAsync(
            It.IsAny<MeetingParticipant>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _groups.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AParticipantJoinsTheGroupKeyedOnTheTranslationRoomId()
    {
        // The group name is the whole point of the call, and it must be the id the SERVER
        // broadcasts on — the translation room id, not the meeting room's primary key.
        RoomExists();
        ParticipantExists(true);
        var hub = CreateHub();

        await hub.JoinMeetingRoom(TranslationRoomId);

        _groups.Verify(g => g.AddToGroupAsync(
            "connection-1",
            MeetingChatHub.GetRoomGroupName(TranslationRoomId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheRoomsCreatorIsAdmittedWithoutAParticipantRow()
    {
        // A host lands on the page before anything has registered them, and
        // MeetingChatService already treats CreatedBy as sufficient. The two must agree.
        RoomExists(createdBy: UserId);
        ParticipantExists(false);
        var hub = CreateHub();

        await hub.JoinMeetingRoom(TranslationRoomId);

        _groups.Verify(g => g.AddToGroupAsync(
            "connection-1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoiningNeverMutatesAnything()
    {
        // It used to reactivate an inactive participant, which is a claim that the user is back
        // in the MEETING made by the chat socket. The join owns that.
        RoomExists();
        ParticipantExists(true);
        var hub = CreateHub();

        await hub.JoinMeetingRoom(TranslationRoomId);

        _participants.Verify(p => p.Update(It.IsAny<MeetingParticipant>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── and it must not report a race as a refusal ──────────────────────────

    [Fact]
    public async Task AnUnprovisionedRoomIsToldApartFromARefusal()
    {
        // "Room not found" read as permanent and fatal, so the client gave up. The row is
        // created by MeetingRoomService.JoinMeetingAsync, which the page calls at the same
        // moment — on a first entry it simply is not there yet.
        RoomMissing();
        var hub = CreateHub();

        var error = await Assert.ThrowsAsync<HubException>(() => hub.JoinMeetingRoom(TranslationRoomId));

        Assert.Equal(MeetingChatHub.RoomNotReady, error.Message);
        Assert.NotEqual(MeetingChatHub.NotAParticipant, error.Message);
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefusedBeforeAnyLookup()
    {
        var hub = CreateHub(authenticated: false);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinMeetingRoom(TranslationRoomId));

        _rooms.Verify(r => r.FirstOrDefaultAsync(
            It.IsAny<Expression<Func<MeetingRoom, bool>>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
