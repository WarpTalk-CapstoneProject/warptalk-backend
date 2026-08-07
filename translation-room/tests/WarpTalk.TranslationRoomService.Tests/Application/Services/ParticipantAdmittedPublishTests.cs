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
/// Approve in the People panel is a REST call. The SignalR hub lives in the Gateway process, so
/// admitting a participant used to flip the row and tell nobody: the admitted guest's own
/// participant poll is disabled while they are in the lobby and their room query has no refetch
/// interval, so their client had no way to learn it had been let in and sat on "Waiting for Host"
/// until the guest pressed Refresh Status. It only ever appeared to work because a host who
/// pressed Start Translation afterwards triggered RoomStarted, which re-joins everyone.
///
/// These pin the publish that closes it, on the same relay channel and in the same envelope shape
/// as RoomStarted/RoomEnded. The Gateway half is pinned by
/// TranslationRoomRedisSubscriberServiceTests.ParticipantAdmitted_*.
/// </summary>
public class ParticipantAdmittedPublishTests
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
    private static readonly Guid AdmittedUserId = Guid.NewGuid();

    public ParticipantAdmittedPublishTests()
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
    public async Task AdmitParticipantAsync_PublishesParticipantAdmitted_WithTheAdmittedUserId()
    {
        var participant = Given();

        var result = await _sut.AdmitParticipantAsync(RoomId, ParticipantRowId, HostId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("CONNECTED");

        var published = CapturedPayload();
        using var document = JsonDocument.Parse(published);
        var root = document.RootElement;

        root.GetProperty("Command").GetString().Should().Be("ParticipantAdmitted");
        root.GetProperty("RoomId").GetString().Should().Be(RoomId.ToString());
        // The client compares this against its own user id to decide whether to re-join, so it must
        // be the admitted USER's id — not the participant row id, which the client never sees.
        root.GetProperty("UserId").GetString().Should().Be(AdmittedUserId.ToString());
    }

    /// <summary>
    /// Published after the save, for the same reason RoomEnded is: a client that re-joins on the
    /// event must not be able to observe itself still WAITING.
    /// </summary>
    [Fact]
    public async Task AdmitParticipantAsync_PublishesOnlyAfterTheRowIsPersisted()
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

        await _sut.AdmitParticipantAsync(RoomId, ParticipantRowId, HostId);

        publishedAfterSave.Should().BeTrue();
    }

    /// <summary>
    /// The admission itself must survive a relay failure. An unnotified guest can still press
    /// Refresh Status; failing the host's Approve after the row is already CONNECTED would leave
    /// the two sides permanently disagreeing.
    /// </summary>
    [Fact]
    public async Task AdmitParticipantAsync_StillSucceeds_WhenThePublishFails()
    {
        var participant = Given();
        _redis
            .Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _sut.AdmitParticipantAsync(RoomId, ParticipantRowId, HostId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("CONNECTED");
    }

    [Fact]
    public async Task AdmitParticipantAsync_PublishesNothing_WhenTheCallerIsRefused()
    {
        Given();
        _workspaceMemberDirectory
            .Setup(d => d.IsOwnerOrAdminAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.AdmitParticipantAsync(RoomId, ParticipantRowId, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        _redis.Verify(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private TranslationRoomParticipant Given()
    {
        var room = new TranslationRoom { Id = RoomId, HostId = HostId, WorkspaceId = Guid.NewGuid() };
        var participant = new TranslationRoomParticipant
        {
            Id = ParticipantRowId,
            TranslationRoomId = RoomId,
            UserId = AdmittedUserId,
            Status = "WAITING"
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
