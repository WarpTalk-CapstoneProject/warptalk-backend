using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.PlatformSettings;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// meetings.recording.enabled is read on every recording start: switched off, the next start is
/// refused before any egress is requested (egress is billable); switched back on, the next start
/// goes through — same service instance, no restart.
/// </summary>
public sealed class RecordingSettingTests
{
    [Fact]
    public async Task Recording_start_follows_the_live_kill_switch()
    {
        var source = new InMemoryPlatformSettingsSource();
        var reader = new PlatformSettingsReader(source, NullLogger<PlatformSettingsReader>.Instance, cacheTtl: TimeSpan.Zero);
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = hostId, ProviderRoomName = "room-1" };

        var unitOfWork = new Mock<IUnitOfWork>();
        var rooms = new Mock<IMeetingRoomRepository>();
        rooms.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(meetingRoom);
        unitOfWork.Setup(u => u.MeetingRoomRepository).Returns(rooms.Object);
        var redis = new Mock<IRedisService>();
        redis.Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>())).ReturnsAsync(Result.Success());
        redis.Setup(r => r.PublishStreamMessageAsync(It.IsAny<string>(), It.IsAny<System.Collections.Generic.Dictionary<string, string>>()))
            .ReturnsAsync(Result.Success());
        redis.Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        var grpc = new Mock<ITranslationRoomGrpcService>();
        grpc.Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));
        var egress = new Mock<ILiveKitEgressService>();
        egress.Setup(e => e.StartRoomCompositeEgressAsync("room-1", It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success("egress-1"));

        var service = new MeetingRoomService(
            new Mock<ILiveKitTokenService>().Object, grpc.Object, unitOfWork.Object, redis.Object, egress.Object,
            new Mock<ILiveKitRoomAdminService>().Object, Mock.Of<ILogger<MeetingRoomService>>(), reader);

        source.Set(PlatformSettingsCatalog.RecordingEnabled, false);
        var refused = await service.SetRecordingAsync(translationRoomId, hostId, "start");
        Assert.Equal(ErrorCodes.Forbidden, refused.ErrorCode);
        Assert.Equal(MeetingRoomService.RecordingDisabledMessage, refused.Error);
        egress.Verify(e => e.StartRoomCompositeEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        source.Set(PlatformSettingsCatalog.RecordingEnabled, true);
        var started = await service.SetRecordingAsync(translationRoomId, hostId, "start");
        Assert.True(started.IsSuccess, started.Error);
        Assert.Equal("egress-1", meetingRoom.ActiveEgressId);
    }
}
