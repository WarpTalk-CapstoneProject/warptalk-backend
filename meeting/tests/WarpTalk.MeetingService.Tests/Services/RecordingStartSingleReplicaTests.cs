using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Coordination;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// k8s multi-replica dedupe. Two Start Recording presses on two replicas both saw no active egress,
/// both started one, and the second overwrote the first one's id — an orphaned recording nobody
/// could stop and two files for one meeting. Start runs under a per-room lease and re-checks the
/// row once it holds it.
/// </summary>
public sealed class RecordingStartSingleReplicaTests
{
    private readonly Guid _translationRoomId = Guid.NewGuid();
    private readonly Guid _hostId = Guid.NewGuid();
    private readonly Mock<IMeetingRoomRepository> _rooms = new();
    private readonly Mock<ILiveKitEgressService> _egress = new();
    private readonly IDistributedLockProvider _locks =
        new DistributedLockProvider(new InProcessLeaseStore(TimeProvider.System), TimeProvider.System);

    private MeetingRoomService NewReplica()
    {
        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(), TranslationRoomId = _translationRoomId, ActiveHostId = _hostId, ProviderRoomName = "room-1"
        };
        _rooms.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(meetingRoom);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.MeetingRoomRepository).Returns(_rooms.Object);

        var redis = new Mock<IRedisService>();
        redis.Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>())).ReturnsAsync(Result.Success());
        redis.Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        var grpc = new Mock<ITranslationRoomGrpcService>();
        grpc.Setup(g => g.GetRoomDetailsAsync(_translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = _hostId.ToString() }));
        _egress.Setup(e => e.StartRoomCompositeEgressAsync("room-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("egress-1"));

        return new MeetingRoomService(
            new Mock<ILiveKitTokenService>().Object, grpc.Object, unitOfWork.Object, redis.Object, _egress.Object,
            new Mock<ILiveKitRoomAdminService>().Object, Mock.Of<ILogger<MeetingRoomService>>(),
            platformSettings: null, locks: _locks);
    }

    private void VerifyEgressStarted(Times times) =>
        _egress.Verify(e => e.StartRoomCompositeEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task Start_WhileAnotherReplicaIsStartingTheRecording_StartsNoSecondEgress()
    {
        var service = NewReplica();
        await using var otherReplica = await _locks.TryAcquireAsync(
            MeetingRoomService.RecordingStartLockResource(_translationRoomId), TimeSpan.FromSeconds(30));
        Assert.NotNull(otherReplica);

        var result = await service.SetRecordingAsync(_translationRoomId, _hostId, "start");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        VerifyEgressStarted(Times.Never());
    }

    [Fact]
    public async Task Start_AfterAnotherReplicaStartedTheRecording_SeesTheRowNotItsStaleCopy()
    {
        var service = NewReplica();
        // This replica's tracked copy still says no egress; the row says otherwise.
        _rooms.Setup(r => r.AnyAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await service.SetRecordingAsync(_translationRoomId, _hostId, "start");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        VerifyEgressStarted(Times.Never());
    }

    [Fact]
    public async Task Start_WithNoRecordingRunning_StartsOneEgress()
    {
        var service = NewReplica();

        var result = await service.SetRecordingAsync(_translationRoomId, _hostId, "start");

        Assert.True(result.IsSuccess, result.Error);
        VerifyEgressStarted(Times.Once());
    }
}
