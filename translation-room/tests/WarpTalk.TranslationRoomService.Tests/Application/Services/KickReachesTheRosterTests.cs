using FluentAssertions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-564: a kick has to reach the roster, or it is only a disconnection.
///
/// The host's kick button calls MeetingService, which evicts the participant from LiveKit,
/// deactivates its own participant row and revokes the meeting invitation. None of that touches
/// this service — and KICKED, the terminal status this service's join refuses on (BR-010), lives
/// here. The roster row was therefore left CONNECTED, became DISCONNECTED when the socket dropped,
/// and the rejoin path reads DISCONNECTED as proof of admission: straight back in, no lobby.
///
/// Confirmed in production before the fix — the kicked participant's row read DISCONNECTED.
///
/// This is the write MeetingService now makes over gRPC, and it re-authorizes host identity
/// against THIS service's tables rather than trusting the caller, exactly as TransferHostAsync
/// does. Host authority is read out of these tables on every join, so this service is the one that
/// has to agree the kick was legitimate.
/// </summary>
public class KickReachesTheRosterTests
{
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly TranslationRoomDirectoryService _sut;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid VisitorId = Guid.NewGuid();

    public KickReachesTheRosterTests()
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participants.Object);

        _sut = new TranslationRoomDirectoryService(
            _rooms.Object,
            _participants.Object,
            _unitOfWork.Object);
    }

    private void RoomExists() =>
        _rooms.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = RoomId, HostId = HostId });

    private TranslationRoomParticipant? ParticipantOnTheRoster(string? status)
    {
        TranslationRoomParticipant? participant = status is null
            ? null
            : new TranslationRoomParticipant
            {
                Id = Guid.NewGuid(),
                TranslationRoomId = RoomId,
                UserId = VisitorId,
                DisplayName = "Visitor",
                Role = "PARTICIPANT",
                Status = status,
                ListenLanguage = "vi",
                SpeakLanguage = "en",
            };

        _participants
            .Setup(p => p.GetByRoomAndUserAsync(RoomId, VisitorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        return participant;
    }

    [Fact]
    public async Task TheKickWritesTheTerminalStatus()
    {
        RoomExists();
        var participant = ParticipantOnTheRoster(TranslationRoomParticipantStatuses.Connected)!;

        var result = await _sut.KickParticipantByUserAsync(RoomId, HostId, VisitorId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        // The whole point: DISCONNECTED would be readmitted on the next join.
        participant.Status.Should().Be(TranslationRoomParticipantStatuses.Kicked);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SomebodyWaitingInTheLobbyCanBeKickedToo()
    {
        // A host rejecting a knock must be as terminal as removing someone from the room; WT-563
        // otherwise leaves them able to knock again forever.
        RoomExists();
        var participant = ParticipantOnTheRoster(TranslationRoomParticipantStatuses.Waiting)!;

        await _sut.KickParticipantByUserAsync(RoomId, HostId, VisitorId);

        participant.Status.Should().Be(TranslationRoomParticipantStatuses.Kicked);
    }

    [Fact]
    public async Task OnlyTheHostMayKick()
    {
        RoomExists();
        var participant = ParticipantOnTheRoster(TranslationRoomParticipantStatuses.Connected)!;

        var result = await _sut.KickParticipantByUserAsync(RoomId, VisitorId, VisitorId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        // Authorization is re-asked here rather than trusted from MeetingService, so a refusal has
        // to leave the roster untouched.
        participant.Status.Should().Be(TranslationRoomParticipantStatuses.Connected);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheHostCannotBeKickedOutOfTheirOwnRoom()
    {
        RoomExists();

        var result = await _sut.KickParticipantByUserAsync(RoomId, HostId, HostId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task NobodyOnTheRosterIsNotAFailure()
    {
        // MeetingService evicted somebody this service never recorded. Reporting failure would make
        // the host retry a kick that already worked.
        RoomExists();
        ParticipantOnTheRoster(null);

        var result = await _sut.KickParticipantByUserAsync(RoomId, HostId, VisitorId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
    }

    [Fact]
    public async Task KickingTwiceIsNotAnError()
    {
        // The host can press the button twice, and MeetingService retries.
        RoomExists();
        ParticipantOnTheRoster(TranslationRoomParticipantStatuses.Kicked);

        var result = await _sut.KickParticipantByUserAsync(RoomId, HostId, VisitorId);

        result.IsSuccess.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AMissingRoomIsNotFound()
    {
        _rooms.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);

        var result = await _sut.KickParticipantByUserAsync(RoomId, HostId, VisitorId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
