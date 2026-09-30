using FluentAssertions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-699 / TC2402: a lobby Reject has to reach the waiting-room row.
///
/// Reject was a MeetingService action that revoked its own session grant and never touched this
/// service, so the row stayed WAITING — still in the host's lobby list, still admittable — and
/// before the call had started it failed outright ("Meeting room not started"). The row now moves
/// to REJECTED here, the join path refuses on it, and the person's lobby tab is told.
/// </summary>
public class RejectReachesTheRosterTests
{
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRedisStateRepository> _redis = new();
    private readonly TranslationRoomDirectoryService _sut;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid VisitorId = Guid.NewGuid();

    public RejectReachesTheRosterTests()
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participants.Object);
        _rooms.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = RoomId, HostId = HostId });

        _sut = new TranslationRoomDirectoryService(
            _rooms.Object,
            _participants.Object,
            _unitOfWork.Object,
            _redis.Object);
    }

    private TranslationRoomParticipant Knocking(string status)
    {
        var participant = new TranslationRoomParticipant
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

    [Theory]
    [InlineData(TranslationRoomParticipantStatuses.Waiting)]
    [InlineData(TranslationRoomParticipantStatuses.Invited)]
    public async Task RejectingAKnockWritesTheTerminalStatusAndTellsTheLobbyTab(string status)
    {
        var participant = Knocking(status);

        var result = await _sut.RejectParticipantByUserAsync(RoomId, HostId, VisitorId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(RosterRemovalOutcome.Removed);
        participant.Status.Should().Be(TranslationRoomParticipantStatuses.Rejected);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _redis.Verify(
            r => r.PublishAsync(
                "warptalk:translation-room:commands",
                It.Is<string>(json => json.Contains("\"ParticipantRejected\"") && json.Contains(VisitorId.ToString()))),
            Times.Once);
    }

    [Fact]
    public async Task RejectingTwiceIsIdempotentAndSaysSo()
    {
        Knocking(TranslationRoomParticipantStatuses.Rejected);

        var result = await _sut.RejectParticipantByUserAsync(RoomId, HostId, VisitorId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(RosterRemovalOutcome.AlreadyRemoved);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnlyTheHostMayReject()
    {
        var participant = Knocking(TranslationRoomParticipantStatuses.Waiting);

        var result = await _sut.RejectParticipantByUserAsync(RoomId, Guid.NewGuid(), VisitorId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        participant.Status.Should().Be(TranslationRoomParticipantStatuses.Waiting);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Somebody already admitted is removed with Kick; "reject" is the wrong verb.</summary>
    [Theory]
    [InlineData(TranslationRoomParticipantStatuses.Connected)]
    [InlineData(TranslationRoomParticipantStatuses.Kicked)]
    public async Task AnAdmittedOrRemovedParticipantCannotBeRejected(string status)
    {
        var participant = Knocking(status);

        var result = await _sut.RejectParticipantByUserAsync(RoomId, HostId, VisitorId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        participant.Status.Should().Be(status);
    }

    [Fact]
    public async Task NobodyWaitingIsARefusedPrecondition()
    {
        _participants
            .Setup(p => p.GetByRoomAndUserAsync(RoomId, VisitorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        var result = await _sut.RejectParticipantByUserAsync(RoomId, HostId, VisitorId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }
}
