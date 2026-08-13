using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-359 / WT-358 / WT-353 — one bug wearing three tickets.
///
/// "Who is the host" had two answers in two services. MeetingService.TransferHostAsync wrote
/// meeting.meeting_rooms.active_host_id; this service went on reading translation_rooms.host_id,
/// which is the BOOKER and never moves. So:
///
///   - the outgoing host rejoined and BR-004 re-stamped their role to HOST (WT-359),
///   - the People panel kept showing them as host until a reload (WT-358),
///   - and the incoming host could not end the meeting, because every host gate here compared
///     against host_id (WT-353).
///
/// The fix is <see cref="TranslationRoom.ActiveHostId"/> plus
/// <see cref="TranslationRoom.IsHostedBy"/>, and these tests pin the three behaviours that were
/// wrong. The first one is the literal reproduction from the ticket.
/// </summary>
public class TransferHostAuthorityTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid Booker = Guid.NewGuid();
    private static readonly Guid Transferee = Guid.NewGuid();

    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly TranslationRoomDirectoryService _sut;

    public TransferHostAuthorityTests()
    {
        _sut = new TranslationRoomDirectoryService(
            _roomRepository.Object,
            _participantRepository.Object,
            _unitOfWork.Object);
    }

    private static TranslationRoom Room(Guid? activeHostId = null) => new()
    {
        Id = RoomId,
        HostId = Booker,
        ActiveHostId = activeHostId,
        Title = "Standup",
        TranslationRoomCode = "ABC123",
        Status = "IN_PROGRESS",
        TranslationRoomType = "INSTANT",
        SourceLanguage = "en",
        TargetLanguages = "[\"vi\"]",
        Settings = "{}"
    };

    private static TranslationRoomParticipant Participant(Guid userId, string role) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = RoomId,
        UserId = userId,
        DisplayName = "Someone",
        Role = role,
        Status = TranslationRoomParticipantStatuses.Connected
    };

    private void GivenRoom(TranslationRoom room) =>
        _roomRepository
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

    private void GivenParticipant(Guid userId, TranslationRoomParticipant? participant) =>
        _participantRepository
            .Setup(r => r.GetByRoomAndUserAsync(RoomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

    /// <summary>
    /// THE TICKET, verbatim: transfer the room away, and the booker is no longer the host —
    /// including on the predicate every host-gated operation in this service consults.
    /// </summary>
    [Fact]
    public async Task AfterTransfer_TheBookerIsNoLongerTheHost()
    {
        var room = Room();
        GivenRoom(room);
        GivenParticipant(Transferee, Participant(Transferee, nameof(TranslationRoomParticipantRole.PARTICIPANT)));
        GivenParticipant(Booker, Participant(Booker, nameof(TranslationRoomParticipantRole.HOST)));

        var result = await _sut.TransferHostAsync(RoomId, Booker, Transferee);

        result.IsSuccess.Should().BeTrue();
        room.ActiveHostId.Should().Be(Transferee);
        room.EffectiveHostId.Should().Be(Transferee);
        room.IsHostedBy(Booker).Should().BeFalse("the booker handed the room over");
        room.IsHostedBy(Transferee).Should().BeTrue();

        // host_id is deliberately untouched: it owns the booking, the series and the usage
        // attribution, none of which a mid-meeting handover transfers.
        room.HostId.Should().Be(Booker);
    }

    /// <summary>
    /// WT-359's reproduction step 4. This is the assertion that would have failed before the fix:
    /// the outgoing host rejoins, and BR-004 must NOT hand them the room back.
    /// </summary>
    [Fact]
    public async Task OutgoingHostRejoining_IsDemotedInsteadOfBeingHandedTheRoomBack()
    {
        var room = Room();
        GivenRoom(room);
        GivenParticipant(Transferee, Participant(Transferee, nameof(TranslationRoomParticipantRole.PARTICIPANT)));

        var bookersRow = Participant(Booker, nameof(TranslationRoomParticipantRole.HOST));
        GivenParticipant(Booker, bookersRow);

        await _sut.TransferHostAsync(RoomId, Booker, Transferee);

        // The transfer itself demotes them...
        bookersRow.Role.Should().Be(nameof(TranslationRoomParticipantRole.PARTICIPANT));

        // ...and rejoining does not undo it. This is the exact call JoinTranslationRoomAsync makes,
        // with isHost computed the way it now computes it.
        bookersRow.Status = TranslationRoomParticipantStatuses.Left;
        bookersRow.UpdateFrom(
            new JoinTranslationRoomRequest("ABC123", "Booker", "en", "vi"),
            speakLanguage: "en",
            listenLanguage: "vi",
            requiresApproval: false,
            isHost: room.IsHostedBy(Booker));

        bookersRow.Role.Should().Be(
            nameof(TranslationRoomParticipantRole.PARTICIPANT),
            "rejoining after a transfer must not restore host — that was the whole of WT-359");
    }

    /// <summary>WT-358: the incoming host's row is HOST immediately, not after a reload.</summary>
    [Fact]
    public async Task Transfer_PromotesTheIncomingHostsRosterRow()
    {
        var room = Room();
        GivenRoom(room);

        var transfereesRow = Participant(Transferee, nameof(TranslationRoomParticipantRole.PARTICIPANT));
        GivenParticipant(Transferee, transfereesRow);
        GivenParticipant(Booker, Participant(Booker, nameof(TranslationRoomParticipantRole.HOST)));

        await _sut.TransferHostAsync(RoomId, Booker, Transferee);

        transfereesRow.Role.Should().Be(nameof(TranslationRoomParticipantRole.HOST));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// WT-359's expected behaviour is explicit: the outgoing host gets the room back if and only if
    /// the incoming host transfers it back. So the booker cannot simply take it.
    /// </summary>
    [Fact]
    public async Task TheBookerCannotTakeTheRoomBackOnTheirOwn()
    {
        GivenRoom(Room(activeHostId: Transferee));

        var result = await _sut.TransferHostAsync(RoomId, Booker, Booker);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>...but the incoming host handing it back is the sanctioned route, and it clears
    /// the column rather than storing "the booker is the active host", which would be a no-op value
    /// that later reads have to special-case.</summary>
    [Fact]
    public async Task TransferringBackToTheBooker_ClearsTheColumn()
    {
        var room = Room(activeHostId: Transferee);
        GivenRoom(room);
        GivenParticipant(Booker, Participant(Booker, nameof(TranslationRoomParticipantRole.PARTICIPANT)));
        GivenParticipant(Transferee, Participant(Transferee, nameof(TranslationRoomParticipantRole.HOST)));

        var result = await _sut.TransferHostAsync(RoomId, Transferee, Booker);

        result.IsSuccess.Should().BeTrue();
        room.ActiveHostId.Should().BeNull();
        room.IsHostedBy(Booker).Should().BeTrue();
    }

    /// <summary>
    /// Host authority that points at somebody with no roster row would be unreachable by every
    /// host-gated operation in this service — a room with no usable host.
    /// </summary>
    [Fact]
    public async Task Transfer_IsRefused_WhenTheTargetIsNotInTheRoom()
    {
        GivenRoom(Room());
        GivenParticipant(Transferee, null);

        var result = await _sut.TransferHostAsync(RoomId, Booker, Transferee);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// MeetingService retries, and the Gateway's host-offline handling can race a deliberate
    /// transfer to the same person. Neither is an error, and neither should record a handover from
    /// someone to themselves.
    /// </summary>
    [Fact]
    public async Task TransferringToTheCurrentHost_SucceedsAndWritesNothing()
    {
        GivenRoom(Room(activeHostId: Transferee));

        var result = await _sut.TransferHostAsync(RoomId, Transferee, Transferee);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Transferee);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// WT-353's second half. Every host gate here compares against the effective host, so a
    /// transfer moves the right to end, stop, pause and reconfigure the meeting along with the
    /// title — rather than leaving them behind with the booker.
    /// </summary>
    [Fact]
    public void HostGates_FollowTheTransfer()
    {
        var transferred = Room(activeHostId: Transferee);

        transferred.IsHostedBy(Transferee).Should().BeTrue();
        transferred.IsHostedBy(Booker).Should().BeFalse();

        var untouched = Room();
        untouched.IsHostedBy(Booker).Should().BeTrue("a room nobody took over is still the booker's");
    }
}
