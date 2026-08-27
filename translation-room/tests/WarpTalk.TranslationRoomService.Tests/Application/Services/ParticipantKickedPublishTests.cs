using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-572: a kicked participant stayed in the meeting.
///
/// Two of the three links were already built and had been for months. The web client has handled
/// the "ParticipantKicked" SignalR event since BR-159 — toast, close the meeting, replace the
/// route — and TranslationRoomRedisSubscriberService has turned a "Kick" relay command into that
/// event for just as long. Nothing published the command. So the host pressed Kick, the roster
/// went terminal, LiveKit evicted them, and the person's own tab was told nothing: it reconnected
/// and carried on receiving transcript. The only thing that ever stopped them was sending a chat
/// message, which is the one surface that re-reads the roster per action — which is exactly the
/// shape of the bug report.
///
/// These pin the missing producer. The Gateway half is pinned by
/// TranslationRoomRedisSubscriberServiceTests; the browser half by the handler in
/// persistent-meeting-session.tsx.
/// </summary>
public class ParticipantKickedPublishTests
{
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";

    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepository = new();
    private readonly Mock<ITranslationRoomInvitationRepository> _invitationRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IWorkspaceMemberDirectory> _workspaceMemberDirectory = new();
    private readonly Mock<IRedisStateRepository> _redis = new();
    private readonly TranslationRoomParticipantService _sut;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid ParticipantRowId = Guid.NewGuid();
    private static readonly Guid KickedUserId = Guid.NewGuid();

    public ParticipantKickedPublishTests()
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_roomRepository.Object);
        _unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participantRepository.Object);
        _unitOfWork.Setup(u => u.TranslationRoomInvitationRepository).Returns(_invitationRepository.Object);

        _sut = new TranslationRoomParticipantService(
            _unitOfWork.Object,
            _workspaceMemberDirectory.Object,
            Mock.Of<ILogger<TranslationRoomParticipantService>>(),
            _redis.Object);
    }

    [Fact]
    public async Task KickParticipantAsync_PublishesKick_WithTheKickedUserId()
    {
        var participant = Given();

        var result = await _sut.KickParticipantAsync(RoomId, ParticipantRowId, HostId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("KICKED");

        var published = CapturedPayload();
        using var document = JsonDocument.Parse(published);
        var root = document.RootElement;

        // "Kick" on the wire, "ParticipantKicked" at the browser. The relay renames it, and that
        // asymmetry is why the missing producer did not turn up by grepping for the event name.
        root.GetProperty("Command").GetString().Should().Be("Kick");
        // The relay needs this to pick the room group to broadcast into; without it the event
        // reaches nobody even though the publish succeeded.
        root.GetProperty("RoomId").GetString().Should().Be(RoomId.ToString());
        // The client compares this against its own user id to decide whether IT was the one
        // removed — so it must be the kicked USER's id, not the participant row id.
        root.GetProperty("UserId").GetString().Should().Be(KickedUserId.ToString());
    }

    /// <summary>
    /// Published after the save, for the same reason the admitted event is: a client acting on
    /// the event must not be able to observe itself still CONNECTED.
    /// </summary>
    [Fact]
    public async Task KickParticipantAsync_PublishesOnlyAfterTheRowIsPersisted()
    {
        Given();

        var saved = false;
        var publishedAfterSave = false;
        _unitOfWork
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1)
            .Callback(() => saved = true);
        _redis
            .Setup(r => r.PublishAsync(GatewayCommandsChannel, It.IsAny<string>()))
            .ReturnsAsync(1)
            .Callback(() => publishedAfterSave = saved);

        await _sut.KickParticipantAsync(RoomId, ParticipantRowId, HostId);

        publishedAfterSave.Should().BeTrue();
    }

    /// <summary>
    /// The kick must survive a relay failure. KICKED is what stops the rejoin (BR-010) and the
    /// LiveKit eviction still follows; failing the host's action after the terminal status is
    /// written would leave the host believing the person is still present when the room service
    /// says otherwise.
    /// </summary>
    [Fact]
    public async Task KickParticipantAsync_StillSucceeds_WhenThePublishFails()
    {
        var participant = Given();
        _redis
            .Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _sut.KickParticipantAsync(RoomId, ParticipantRowId, HostId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("KICKED");
    }

    /// <summary>
    /// Kicking is host-only and stays that way — see the WT-313 note on the method. A refused
    /// caller must not be able to make somebody else's browser leave the room.
    /// </summary>
    [Fact]
    public async Task KickParticipantAsync_PublishesNothing_WhenTheCallerIsNotTheHost()
    {
        Given();

        var result = await _sut.KickParticipantAsync(RoomId, ParticipantRowId, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        _redis.Verify(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// The host cannot be kicked, and the guard runs before the status write — so nothing is
    /// published either.
    /// </summary>
    [Fact]
    public async Task KickParticipantAsync_PublishesNothing_WhenTheTargetIsTheHost()
    {
        var room = new TranslationRoom { Id = RoomId, HostId = HostId, WorkspaceId = Guid.NewGuid() };
        var hostRow = new TranslationRoomParticipant
        {
            Id = ParticipantRowId,
            TranslationRoomId = RoomId,
            UserId = HostId,
            Status = "CONNECTED"
        };

        _roomRepository.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        _participantRepository.Setup(r => r.GetByIdAsync(ParticipantRowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostRow);
        _redis.Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(1);

        var result = await _sut.KickParticipantAsync(RoomId, ParticipantRowId, HostId);

        result.IsSuccess.Should().BeFalse();
        hostRow.Status.Should().Be("CONNECTED");
        _redis.Verify(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private TranslationRoomParticipant Given()
    {
        var room = new TranslationRoom { Id = RoomId, HostId = HostId, WorkspaceId = Guid.NewGuid() };
        var participant = new TranslationRoomParticipant
        {
            Id = ParticipantRowId,
            TranslationRoomId = RoomId,
            UserId = KickedUserId,
            Status = "CONNECTED"
        };

        _roomRepository.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        _participantRepository.Setup(r => r.GetByIdAsync(ParticipantRowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        _redis.Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(1);

        return participant;
    }

    private string CapturedPayload()
    {
        var invocation = Assert.Single(
            _redis.Invocations,
            i => i.Method.Name == nameof(IRedisStateRepository.PublishAsync)
                 && (string)i.Arguments[0] == GatewayCommandsChannel);

        return (string)invocation.Arguments[1];
    }
}
