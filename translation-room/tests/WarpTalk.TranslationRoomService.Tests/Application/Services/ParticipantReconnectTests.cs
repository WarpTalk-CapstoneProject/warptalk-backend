using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// The return half of a dropped socket. MarkParticipantDisconnectedAsync writes DISCONNECTED when
/// the hub socket drops, and nothing wrote it back when SignalR reconnected — so after one network
/// blip the idle reaper, which reads that row, ended a sole participant's meeting while they were
/// still in it. Every EXTERNAL_BRIDGE room is a sole participant.
///
/// The hub publishes participant-online on EVERY join, so most of these pin what must NOT move.
/// </summary>
public sealed class ParticipantReconnectTests
{
    private static readonly Guid HostId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GuestId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly TranslationRoomParticipantService _sut;

    private readonly TranslationRoom _room = new()
    {
        Id = Guid.NewGuid(),
        HostId = HostId,
        Status = "IN_PROGRESS",
        MaxParticipants = 2,
    };

    public ParticipantReconnectTests()
    {
        _unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _unitOfWork.SetupGet(u => u.TranslationRoomParticipantRepository).Returns(_participants.Object);
        _rooms.Setup(r => r.GetByIdAsync(_room.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_room);

        _sut = new TranslationRoomParticipantService(
            _unitOfWork.Object,
            Mock.Of<IWorkspaceMemberDirectory>(),
            NullLogger<TranslationRoomParticipantService>.Instance);
    }

    private TranslationRoomParticipant Row(Guid userId, string status)
    {
        var participant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = _room.Id,
            UserId = userId,
            DisplayName = "Someone",
            Role = "PARTICIPANT",
            ListenLanguage = "vi",
            SpeakLanguage = "en",
            Status = status,
            ConnectionType = "WEBRTC",
        };

        _participants
            .Setup(r => r.GetByRoomAndUserAsync(_room.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        return participant;
    }

    private void SeatsTaken(int seats) =>
        _participants
            .Setup(r => r.CountSeatHoldingParticipantsAsync(_room.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(seats);

    [Fact]
    public async Task ADisconnectedHostWhoseSocketCameBack_IsConnectedAgain()
    {
        var host = Row(HostId, TranslationRoomParticipantStatuses.Disconnected);

        var result = await _sut.MarkParticipantReconnectedAsync(_room.Id, HostId);

        result.IsSuccess.Should().BeTrue();
        host.Status.Should().Be(TranslationRoomParticipantStatuses.Connected);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheHostIsReseatedEvenInAFullRoom()
    {
        // The same exemption the REST join makes: the host is never locked out of their own room.
        var host = Row(HostId, TranslationRoomParticipantStatuses.Disconnected);
        SeatsTaken(2);

        await _sut.MarkParticipantReconnectedAsync(_room.Id, HostId);

        host.Status.Should().Be(TranslationRoomParticipantStatuses.Connected);
    }

    [Fact]
    public async Task AGuestIsReseatedWhenThereIsRoom()
    {
        var guest = Row(GuestId, TranslationRoomParticipantStatuses.Disconnected);
        SeatsTaken(1);

        await _sut.MarkParticipantReconnectedAsync(_room.Id, GuestId);

        guest.Status.Should().Be(TranslationRoomParticipantStatuses.Connected);
    }

    [Fact]
    public async Task AGuestIsNotReseatedPastTheCap()
    {
        // DISCONNECTED released the seat; somebody else may have taken it meanwhile.
        var guest = Row(GuestId, TranslationRoomParticipantStatuses.Disconnected);
        SeatsTaken(2);

        await _sut.MarkParticipantReconnectedAsync(_room.Id, GuestId);

        guest.Status.Should().Be(TranslationRoomParticipantStatuses.Disconnected);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    // A knock is not an admission (WT-563): a lobby socket must not walk anybody in.
    [InlineData(TranslationRoomParticipantStatuses.Waiting)]
    [InlineData(TranslationRoomParticipantStatuses.Invited)]
    // LEFT re-enters through the REST join; KICKED and REJECTED are terminal.
    [InlineData(TranslationRoomParticipantStatuses.Left)]
    [InlineData(TranslationRoomParticipantStatuses.Kicked)]
    [InlineData(TranslationRoomParticipantStatuses.Rejected)]
    [InlineData(TranslationRoomParticipantStatuses.Connected)]
    public async Task OnlyADisconnectedRowIsTouched(string status)
    {
        var participant = Row(GuestId, status);

        var result = await _sut.MarkParticipantReconnectedAsync(_room.Id, GuestId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be(status);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("ENDED")]
    [InlineData("CANCELLED")]
    [InlineData("EXPIRED")]
    public async Task NobodyIsReseatedInARoomThatIsOver(string roomStatus)
    {
        // EndTranslationRoomAsync releases every seat by writing DISCONNECTED, so without this a
        // late socket would put somebody back into a finished meeting.
        _room.Status = roomStatus;
        var host = Row(HostId, TranslationRoomParticipantStatuses.Disconnected);

        await _sut.MarkParticipantReconnectedAsync(_room.Id, HostId);

        host.Status.Should().Be(TranslationRoomParticipantStatuses.Disconnected);
    }

    [Fact]
    public async Task AJoinThatRacedItsOwnRegistration_IsNotAnError()
    {
        var result = await _sut.MarkParticipantReconnectedAsync(_room.Id, Guid.NewGuid());

        result.IsSuccess.Should().BeTrue("there is no row yet, and so nothing to restore");
    }
}
