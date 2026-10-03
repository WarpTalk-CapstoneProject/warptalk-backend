using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.MeetingService.API.Workers;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.Coordination;
using RoomResponse = WarpTalk.Shared.Protos.GetTranslationRoomResponse;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// A Google Meet bridge recording used to run on for ~20-24 s after the bridge session ended,
/// because only the Stop button stopped it and LiveKit ended the egress only when it closed the
/// empty room. It now stops when the room has nobody left in it past a short grace (a reload must
/// not cut it), or at once when translation-room has ended the room — through the Stop path.
/// </summary>
public sealed class BridgeRecordingEndTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ITranslationRoomGrpcService> _grpc = new();
    private readonly Mock<IRedisService> _redis = new();
    private readonly Mock<ILiveKitEgressService> _egress = new();
    private readonly Mock<ILiveKitRoomAdminService> _roomAdmin = new();
    private readonly Mock<IDistributedLockProvider> _locks = new();
    private readonly Mock<IDistributedLease> _marker = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IMeetingRoomRepository> _rooms = new();
    private int _saves;
    private readonly List<RtcStreamParticipant> _participantRows = [];
    private readonly List<(string Channel, object Payload)> _published = [];
    private readonly MeetingRoom _room;
    private readonly Guid _translationRoomId = Guid.NewGuid();
    private readonly Guid _hostId = Guid.NewGuid();

    public BridgeRecordingEndTests()
    {
        _room = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = _translationRoomId,
            ProviderRoomName = "room-1",
            ActiveEgressId = "egress-1",
            Status = "IN_PROGRESS",
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
        };

        var rooms = _rooms;
        rooms.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                predicate.Compile()(_room) ? _room : null);
        _unitOfWork.SetupGet(u => u.MeetingRoomRepository).Returns(rooms.Object);

        var participants = new Mock<IRtcStreamParticipantRepository>();
        participants.Setup(r => r.GetLatestDepartureAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid meetingRoomId, CancellationToken _) =>
                _participantRows.Where(p => p.MeetingRoomId == meetingRoomId).Max(p => p.LeftAt));
        participants.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<RtcStreamParticipant, bool>> predicate, string _, CancellationToken _) =>
                _participantRows.FirstOrDefault(predicate.Compile()));
        _unitOfWork.SetupGet(u => u.RtcStreamParticipantRepository).Returns(participants.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Callback(() => _saves++).ReturnsAsync(1);

        _redis.Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()))
            .Callback<string, object>((channel, payload) => _published.Add((channel, payload)))
            .ReturnsAsync(Result.Success());
        SetCachedRoom(null);
        SetRoomDetails(BridgeRoom());

        _egress.Setup(e => e.StopEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));
        SetEgressStatus("EGRESS_ACTIVE");

        _locks.Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_marker.Object);
    }

    private MeetingRoomService CreateService() => new(
        Mock.Of<ILiveKitTokenService>(),
        _grpc.Object,
        _unitOfWork.Object,
        _redis.Object,
        _egress.Object,
        _roomAdmin.Object,
        Mock.Of<ILogger<MeetingRoomService>>(),
        locks: _locks.Object);

    private RoomResponse BridgeRoom(string status = "IN_PROGRESS") => new()
    {
        Id = _translationRoomId.ToString(),
        HostId = _hostId.ToString(),
        TranslationRoomType = ExternalBridgeConstants.RoomType,
        Status = status,
    };

    private void SetCachedRoom(RoomResponse? room) =>
        _redis.Setup(r => r.GetCacheAsync<RoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<RoomResponse?>(room));

    private void SetRoomDetails(RoomResponse room) =>
        _grpc.Setup(g => g.GetRoomDetailsAsync(_translationRoomId)).ReturnsAsync(Result.Success(room));

    private void SetLiveKitParticipants(params LiveKitRoomParticipant[] participants) =>
        _roomAdmin.Setup(a => a.ListParticipantsAsync("room-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<LiveKitRoomParticipant>>(participants));

    private void SetEgressStatus(string status)
    {
        var info = JsonDocument.Parse($"{{\"egressId\":\"egress-1\",\"status\":\"{status}\"}}").RootElement.Clone();
        _egress.Setup(e => e.GetEgressAsync("egress-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<JsonElement?>(info));
    }

    /// <summary>What LiveKit lists in a bridge room nobody is in any more: the recorder and our bot.</summary>
    private static LiveKitRoomParticipant[] OnlyMachinery() =>
    [
        new("EG_abc123", "EGRESS", "ACTIVE"),
        new("AIBot_room-1", null, "ACTIVE"),
        new("ai-interpreter-vi", null, "ACTIVE"),
    ];

    private static BridgeRecordingEndRequest Departure(DateTime at, string egressId = "egress-1") =>
        new("room-1", egressId, at);

    private void VerifyNotStopped()
    {
        _egress.Verify(e => e.StopEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.DoesNotContain(_published, p => p.Channel == "warptalk:translation-room:commands");
    }

    [Fact]
    public async Task EmptyBridgeRoom_PastGrace_StopsThroughTheStopPath()
    {
        SetLiveKitParticipants(OnlyMachinery());

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now - Grace - TimeSpan.FromSeconds(1)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Stopped, check.Outcome);
        _egress.Verify(e => e.StopEgressAsync("egress-1", It.IsAny<CancellationToken>()), Times.Once);
        // Same bookkeeping as the Stop button: id kept for finalization, UpdatedAt moved, everyone told.
        Assert.Equal("egress-1", _room.ActiveEgressId);
        Assert.True(_room.UpdatedAt > DateTime.UtcNow.AddMinutes(-5));
        // Only the stop time, as a conditional UPDATE of that column: the worker's copy of the row
        // may be stale, and Update(row) would write its ActiveHostId & co. back.
        _rooms.Verify(r => r.MarkRecordingStopRequestedAsync(_room.Id, "egress-1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _rooms.Verify(r => r.Update(It.IsAny<MeetingRoom>()), Times.Never);
        var (channel, payload) = Assert.Single(_published);
        Assert.Equal("warptalk:translation-room:commands", channel);
        var fields = Assert.IsType<Dictionary<string, object?>>(payload);
        Assert.Equal("RecordingStateChanged", fields["Command"]);
        Assert.Equal(false, fields["Recording"]);
        // The marker is kept, not released: a late check on another replica must find it.
        _marker.Verify(m => m.DisposeAsync(), Times.Never);
        _marker.Verify(m => m.ReleaseAsync(), Times.Never);
    }

    [Fact]
    public async Task EmptyBridgeRoom_JustEmptied_WaitsForTheGraceInsteadOfStopping()
    {
        SetLiveKitParticipants(OnlyMachinery());

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddSeconds(-2)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.AwaitingGrace, check.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(8), check.RetryAfter);
        VerifyNotStopped();
    }

    [Fact]
    public async Task HostReloadedAndIsBack_RecordingKeepsRunning()
    {
        SetLiveKitParticipants([.. OnlyMachinery(), new(_hostId.ToString(), null, "ACTIVE")]);

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now - TimeSpan.FromSeconds(30)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Occupied, check.Outcome);
        VerifyNotStopped();
    }

    [Fact]
    public async Task StandInStillConnected_TheFarSideIsStillLive_RecordingKeepsRunning()
    {
        SetLiveKitParticipants([.. OnlyMachinery(), new(ExternalBridgeConstants.ParticipantUserId.ToString(), null, "JOINED")]);

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now - TimeSpan.FromSeconds(30)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Occupied, check.Outcome);
        VerifyNotStopped();
    }

    [Fact]
    public async Task SomebodyElseLeftMoreRecently_GraceRunsFromTheirDeparture()
    {
        SetLiveKitParticipants(OnlyMachinery());
        _participantRows.Add(new RtcStreamParticipant { MeetingRoomId = _room.Id, ProviderIdentity = "u2", LeftAt = Now.AddSeconds(-3) });

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddSeconds(-30)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.AwaitingGrace, check.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(7), check.RetryAfter);
        VerifyNotStopped();
    }

    [Fact]
    public async Task RoomEndedByTranslationRoom_StopsAtOnce_WithoutGraceOrPresenceCheck()
    {
        SetRoomDetails(BridgeRoom("ENDED"));
        SetLiveKitParticipants(new LiveKitRoomParticipant(_hostId.ToString(), null, "ACTIVE"));

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Stopped, check.Outcome);
        _egress.Verify(e => e.StopEgressAsync("egress-1", It.IsAny<CancellationToken>()), Times.Once);
        _roomAdmin.Verify(a => a.ListParticipantsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NativeRoom_IsNeverTouched_AndCostsNoLookup()
    {
        SetCachedRoom(new RoomResponse { Id = _translationRoomId.ToString(), TranslationRoomType = "STANDARD" });
        SetLiveKitParticipants(OnlyMachinery());

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddMinutes(-5)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Ignored, check.Outcome);
        VerifyNotStopped();
        _grpc.Verify(g => g.GetRoomDetailsAsync(It.IsAny<Guid>()), Times.Never);
        _roomAdmin.Verify(a => a.ListParticipantsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NativeRoom_ColdCache_IsNeverTouched()
    {
        SetRoomDetails(new RoomResponse { Id = _translationRoomId.ToString(), TranslationRoomType = "STANDARD", Status = "ENDED" });
        SetLiveKitParticipants(OnlyMachinery());

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddMinutes(-5)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Ignored, check.Outcome);
        VerifyNotStopped();
    }

    [Fact]
    public async Task ADifferentRecordingIsRunningNow_TheStaleDepartureDoesNothing()
    {
        SetLiveKitParticipants(OnlyMachinery());

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddMinutes(-5), egressId: "egress-old"), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Ignored, check.Outcome);
        VerifyNotStopped();
    }

    [Fact]
    public async Task AlreadyStoppedByHand_IsNotStoppedAgain()
    {
        SetLiveKitParticipants(OnlyMachinery());
        SetEgressStatus("EGRESS_ENDING");

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddMinutes(-5)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Ignored, check.Outcome);
        VerifyNotStopped();
    }

    [Fact]
    public async Task LiveKitCannotListTheRoom_LeavesTheRecordingRunning()
    {
        _roomAdmin.Setup(a => a.ListParticipantsAsync("room-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<LiveKitRoomParticipant>>("down", "LIVEKIT_ROOM_COMMAND_FAILED"));

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddMinutes(-5)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Failed, check.Outcome);
        VerifyNotStopped();
    }

    [Fact]
    public async Task RoomTypeUnknown_LeavesTheRecordingRunning()
    {
        _grpc.Setup(g => g.GetRoomDetailsAsync(_translationRoomId))
            .ReturnsAsync(Result.Failure<RoomResponse>("unavailable", ErrorCodes.ServiceUnavailable));
        SetLiveKitParticipants(OnlyMachinery());

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddMinutes(-5)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Failed, check.Outcome);
        VerifyNotStopped();
    }

    [Fact]
    public async Task AnotherReplicaAlreadyStoppingIt_DoesNothing()
    {
        SetLiveKitParticipants(OnlyMachinery());
        _locks.Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDistributedLease?)null);

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddMinutes(-5)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Ignored, check.Outcome);
        VerifyNotStopped();
        _locks.Verify(l => l.TryAcquireAsync(MeetingRoomService.BridgeRecordingEndMarkerResource("egress-1"), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task StopFails_TheMarkerIsGivenBack_SoALaterCheckCanRetry()
    {
        SetLiveKitParticipants(OnlyMachinery());
        _egress.Setup(e => e.StopEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<bool>("nope", "LIVEKIT_EGRESS_STOP_FAILED"));

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddMinutes(-5)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Failed, check.Outcome);
        _marker.Verify(m => m.DisposeAsync(), Times.Once);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task ManualStop_StillWorksThroughTheSharedStopPath()
    {
        SetCachedRoom(BridgeRoom());

        var result = await CreateService().SetRecordingAsync(_translationRoomId, _hostId, "stop");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Recording);
        _egress.Verify(e => e.StopEgressAsync("egress-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("egress-1", _room.ActiveEgressId);
    }

    [Fact]
    public async Task SaveOrPublishThrowsAfterLiveKitStopped_StillReportsStopped()
    {
        SetLiveKitParticipants(OnlyMachinery());
        _rooms.Setup(r => r.MarkRecordingStopRequestedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        _redis.Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var check = await CreateService().StopRecordingIfBridgeEndedAsync(Departure(Now.AddMinutes(-5)), Now, Grace);

        Assert.Equal(BridgeRecordingEndOutcome.Stopped, check.Outcome);
        _egress.Verify(e => e.StopEgressAsync("egress-1", It.IsAny<CancellationToken>()), Times.Once);
        _redis.Verify(r => r.PublishEventAsync("warptalk:translation-room:commands", It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task ManualStop_WhenSaveThrowsAfterLiveKitStopped_IsStillStoppedNot500()
    {
        SetCachedRoom(BridgeRoom());
        _rooms.Setup(r => r.MarkRecordingStopRequestedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await CreateService().SetRecordingAsync(_translationRoomId, _hostId, "stop");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Recording);
        Assert.Contains(_published, p => p.Channel == "warptalk:translation-room:commands");
    }

    [Theory]
    [InlineData("EGRESS_ENDING")]
    [InlineData("EGRESS_COMPLETE")]
    [InlineData("EGRESS_ABORTED")]
    public async Task ManualStop_AfterTheAutoStop_LiveKitRefuses_IsStillStopped(string status)
    {
        SetCachedRoom(BridgeRoom());
        _egress.Setup(e => e.StopEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<bool>("failed_precondition", "LIVEKIT_EGRESS_STOP_FAILED"));
        SetEgressStatus(status);

        var result = await CreateService().SetRecordingAsync(_translationRoomId, _hostId, "stop");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Recording);
    }

    [Fact]
    public async Task ManualStop_WhileTheAutoStopHoldsTheMarker_IsStillStopped()
    {
        SetCachedRoom(BridgeRoom());
        _egress.Setup(e => e.StopEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<bool>("failed_precondition", "LIVEKIT_EGRESS_STOP_FAILED"));
        _locks.Setup(l => l.TryAcquireAsync(MeetingRoomService.BridgeRecordingEndMarkerResource("egress-1"), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDistributedLease?)null);

        var result = await CreateService().SetRecordingAsync(_translationRoomId, _hostId, "stop");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ManualStop_RealLiveKitFailure_WhileStillCapturing_Is500_AndTheMarkerProbeIsReleased()
    {
        SetCachedRoom(BridgeRoom());
        _egress.Setup(e => e.StopEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<bool>("unavailable", "LIVEKIT_EGRESS_STOP_FAILED"));

        var result = await CreateService().SetRecordingAsync(_translationRoomId, _hostId, "stop");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
        _marker.Verify(m => m.DisposeAsync(), Times.Once);
        Assert.Empty(_published);
    }

    // ── the policy ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("user-1", null, "ACTIVE", true)]
    [InlineData("user-1", "STANDARD", "JOINED", true)]
    [InlineData("sip-caller", "SIP", null, true)]
    [InlineData("00000000-0000-0000-0000-00000000b21d", null, "ACTIVE", true)] // the bridge stand-in
    [InlineData("user-1", null, "DISCONNECTED", false)]
    [InlineData("EG_xyz", null, "ACTIVE", false)]
    [InlineData("anything", "EGRESS", "ACTIVE", false)]
    [InlineData("ingress-1", "INGRESS", "ACTIVE", false)]
    [InlineData("agent-1", "AGENT", "ACTIVE", false)]
    [InlineData("AIBot_room-1", null, "ACTIVE", false)]
    [InlineData("ai-interpreter-en", null, "ACTIVE", false)]
    [InlineData("", null, "ACTIVE", false)]
    public void CountsAsPresence(string identity, string? kind, string? state, bool expected) =>
        Assert.Equal(expected, BridgeRecordingEndPolicy.CountsAsPresence(new LiveKitRoomParticipant(identity, kind, state)));

    [Theory]
    [InlineData("{\"status\":\"EGRESS_STARTING\"}", true)]
    [InlineData("{\"status\":\"EGRESS_ACTIVE\"}", true)]
    [InlineData("{}", true)]
    [InlineData("{\"status\":1}", true)]
    [InlineData("{\"status\":\"EGRESS_ENDING\"}", false)]
    [InlineData("{\"status\":\"EGRESS_COMPLETE\"}", false)]
    [InlineData("{\"status\":2}", false)]
    public void IsEgressCapturing(string json, bool expected) =>
        Assert.Equal(expected, BridgeRecordingEndPolicy.IsEgressCapturing(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("{\"status\":\"EGRESS_COMPLETE\"}", true)]
    [InlineData("{\"status\":\"egress_failed\"}", true)]
    [InlineData("{\"status\":6}", true)]
    [InlineData("{\"status\":\"EGRESS_ENDING\"}", false)]
    [InlineData("{\"status\":\"SOMETHING_NEW\"}", false)]
    [InlineData("{}", false)]
    public void EgressStatuses_IsTerminal_MatchesTheReconciliationRule(string json, bool expected) =>
        Assert.Equal(expected, EgressStatuses.IsTerminal(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("ENDED", true)]
    [InlineData("FINISHED", true)]
    [InlineData("CANCELLED", true)]
    [InlineData("EXPIRED", true)]
    [InlineData("IN_PROGRESS", false)]
    [InlineData("WAITING", false)]
    [InlineData("ended", false)] // exact, as the join and bridge-token gates always compared
    [InlineData(null, false)]
    public void TranslationRoomStatuses_IsEnded(string? status, bool expected) =>
        Assert.Equal(expected, TranslationRoomStatuses.IsEnded(status));

    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("abc", 10)]
    [InlineData("7", 7)]
    [InlineData("1", 3)]
    [InlineData("600", 60)]
    [InlineData("NaN", 10)]
    [InlineData("Infinity", 10)]
    [InlineData("-Infinity", 10)]
    [InlineData("1e308", 60)]
    [InlineData("-1e308", 3)]
    public void GraceFromSeconds_DefaultsAndClamps(string? configured, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BridgeRecordingEndPolicy.GraceFromSeconds(configured));

    // ── the webhook hands departures over ────────────────────────────────────

    [Theory]
    [InlineData("user-42", null, true)]
    [InlineData("AIBot_room-1", null, false)]
    [InlineData("EG_abc", null, false)]
    [InlineData("recorder", "EGRESS", false)]
    [InlineData("recorder", 2, false)]
    public async Task ParticipantLeft_InARecordingRoom_IsHandedToTheWatcher_UnlessItIsMachinery(string identity, object? kind, bool expected)
    {
        var watcher = new Mock<IBridgeRecordingEndWatcher>();
        var sut = CreateWebhookService(watcher.Object);

        var result = await sut.ProcessWebhookAsync(Left("room-1", identity, kind));

        Assert.True(result.IsSuccess);
        watcher.Verify(
            w => w.NotifyParticipantLeft(It.Is<BridgeRecordingEndRequest>(r => r.ProviderRoomName == "room-1" && r.EgressId == "egress-1")),
            expected ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task ParticipantLeft_WithNoRecordingRunning_IsNotHandedOver()
    {
        _room.ActiveEgressId = null;
        var watcher = new Mock<IBridgeRecordingEndWatcher>();

        var result = await CreateWebhookService(watcher.Object).ProcessWebhookAsync(Left("room-1", "user-42", null));

        Assert.True(result.IsSuccess);
        watcher.Verify(w => w.NotifyParticipantLeft(It.IsAny<BridgeRecordingEndRequest>()), Times.Never);
    }

    [Fact]
    public async Task ParticipantLeft_WatcherThrows_TheWebhookStillSucceeds()
    {
        var watcher = new Mock<IBridgeRecordingEndWatcher>();
        watcher.Setup(w => w.NotifyParticipantLeft(It.IsAny<BridgeRecordingEndRequest>())).Throws(new InvalidOperationException("boom"));

        var result = await CreateWebhookService(watcher.Object).ProcessWebhookAsync(Left("room-1", "user-42", null));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ParticipantLeft_IsHandedOverOnlyAfterTheWebhookSaved()
    {
        var savesWhenNotified = -1;
        var watcher = new Mock<IBridgeRecordingEndWatcher>();
        watcher.Setup(w => w.NotifyParticipantLeft(It.IsAny<BridgeRecordingEndRequest>()))
            .Callback(() => savesWhenNotified = _saves);

        var result = await CreateWebhookService(watcher.Object).ProcessWebhookAsync(Left("room-1", "user-42", null));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, savesWhenNotified);
    }

    [Fact]
    public async Task ParticipantLeft_SaveFails_NothingIsHandedOver()
    {
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        var watcher = new Mock<IBridgeRecordingEndWatcher>();

        var result = await CreateWebhookService(watcher.Object).ProcessWebhookAsync(Left("room-1", "user-42", null));

        Assert.False(result.IsSuccess);
        watcher.Verify(w => w.NotifyParticipantLeft(It.IsAny<BridgeRecordingEndRequest>()), Times.Never);
    }

    private MeetingWebhookService CreateWebhookService(IBridgeRecordingEndWatcher watcher)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LiveKit:ApiSecret"] = "test-webhook-secret-with-at-least-32-characters" })
            .Build();
        return new MeetingWebhookService(
            _unitOfWork.Object,
            _redis.Object,
            Mock.Of<IEgressCompletion>(),
            configuration,
            NullLogger<MeetingWebhookService>.Instance,
            watcher);
    }

    private static JsonElement Left(string roomName, string identity, object? kind)
    {
        var participant = new Dictionary<string, object?> { ["identity"] = identity };
        if (kind is not null) participant["kind"] = kind;
        return JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["event"] = "participant_left",
            ["room"] = new { name = roomName },
            ["participant"] = participant,
        })).RootElement.Clone();
    }

    // ── the worker follows a departure through the grace ─────────────────────

    [Fact]
    public async Task Worker_AsksAgainAfterTheGrace_AndStopsWhenStillEmpty()
    {
        var rooms = new Mock<IMeetingRoomService>();
        rooms.SetupSequence(r => r.StopRecordingIfBridgeEndedAsync(It.IsAny<BridgeRecordingEndRequest>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BridgeRecordingEndCheck(BridgeRecordingEndOutcome.AwaitingGrace, "empty", TimeSpan.FromMilliseconds(10)))
            .ReturnsAsync(new BridgeRecordingEndCheck(BridgeRecordingEndOutcome.Stopped, "room empty past grace"));
        var worker = CreateWorker(rooms.Object);

        var check = await worker.FollowDepartureAsync(Departure(Now), CancellationToken.None);

        Assert.Equal(BridgeRecordingEndOutcome.Stopped, check!.Outcome);
        rooms.Verify(r => r.StopRecordingIfBridgeEndedAsync(It.IsAny<BridgeRecordingEndRequest>(), It.IsAny<DateTime>(), worker.Grace, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Worker_StopsAskingOnceSomebodyIsBack()
    {
        var rooms = new Mock<IMeetingRoomService>();
        rooms.SetupSequence(r => r.StopRecordingIfBridgeEndedAsync(It.IsAny<BridgeRecordingEndRequest>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BridgeRecordingEndCheck(BridgeRecordingEndOutcome.AwaitingGrace, "empty", TimeSpan.FromMilliseconds(10)))
            .ReturnsAsync(new BridgeRecordingEndCheck(BridgeRecordingEndOutcome.Occupied, "back"));
        var worker = CreateWorker(rooms.Object);

        var check = await worker.FollowDepartureAsync(Departure(Now), CancellationToken.None);

        Assert.Equal(BridgeRecordingEndOutcome.Occupied, check!.Outcome);
        rooms.Verify(r => r.StopRecordingIfBridgeEndedAsync(It.IsAny<BridgeRecordingEndRequest>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Worker_IsBounded_WhenTheAnswerKeepsSayingWait()
    {
        var rooms = new Mock<IMeetingRoomService>();
        rooms.Setup(r => r.StopRecordingIfBridgeEndedAsync(It.IsAny<BridgeRecordingEndRequest>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BridgeRecordingEndCheck(BridgeRecordingEndOutcome.AwaitingGrace, "empty", TimeSpan.FromMilliseconds(1)));
        var worker = CreateWorker(rooms.Object);

        await worker.FollowDepartureAsync(Departure(Now), CancellationToken.None);

        rooms.Verify(r => r.StopRecordingIfBridgeEndedAsync(It.IsAny<BridgeRecordingEndRequest>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Exactly(BridgeRecordingEndWorker.MaxChecksPerDeparture));
    }

    [Fact]
    public void Worker_DefaultGraceIsTenSeconds_AndConfigurable()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), CreateWorker(Mock.Of<IMeetingRoomService>()).Grace);
        Assert.Equal(TimeSpan.FromSeconds(6), CreateWorker(Mock.Of<IMeetingRoomService>(), "6").Grace);
    }

    private static BridgeRecordingEndWorker CreateWorker(IMeetingRoomService rooms, string? graceSeconds = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => rooms);
        var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Meeting:BridgeRecording:EndGraceSeconds"] = graceSeconds })
            .Build();
        return new BridgeRecordingEndWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<BridgeRecordingEndWorker>.Instance);
    }

    // ── LiveKit ListParticipants ─────────────────────────────────────────────

    [Fact]
    public void ReadParticipants_AcceptsEnumNamesAndOrdinals()
    {
        var participants = LiveKitRoomAdminService.ReadParticipants("""
            {"participants":[
              {"identity":"user-1","state":"ACTIVE"},
              {"identity":"EG_1","kind":"EGRESS","state":"ACTIVE"},
              {"identity":"rec","kind":2,"state":3},
              {"identity":""}
            ]}
            """);

        Assert.Equal(3, participants.Count);
        Assert.Equal(new LiveKitRoomParticipant("user-1", null, "ACTIVE"), participants[0]);
        Assert.Equal(new LiveKitRoomParticipant("EG_1", "EGRESS", "ACTIVE"), participants[1]);
        Assert.Equal(new LiveKitRoomParticipant("rec", "EGRESS", "DISCONNECTED"), participants[2]);
    }

    [Fact]
    public async Task ListParticipants_RoomLiveKitNoLongerHas_IsEmptyNotAFailure()
    {
        string? requested = null;
        var sut = CreateRoomAdmin(new StubHandler(request =>
        {
            requested = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"code\":\"not_found\"}") };
        }));

        var result = await sut.ListParticipantsAsync("room-gone");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
        Assert.Equal("https://warptalk-staging.livekit.cloud/twirp/livekit.RoomService/ListParticipants", requested);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>404 Not Found</html>")]
    [InlineData("{\"code\":\"bad_route\",\"msg\":\"no handler\"}")]
    public async Task ListParticipants_A404ThatIsNotTwirpNotFound_IsAFailure(string body)
    {
        var sut = CreateRoomAdmin(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(body) }));

        var result = await sut.ListParticipantsAsync("room-1");

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"participants\":[")]
    public async Task ListParticipants_MalformedBody_IsAFailure_NotAnEmptyRoom(string body)
    {
        var sut = CreateRoomAdmin(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));

        var result = await sut.ListParticipantsAsync("room-1");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ListParticipants_ServerError_IsAFailure()
    {
        var sut = CreateRoomAdmin(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }));

        var result = await sut.ListParticipantsAsync("room-1");

        Assert.False(result.IsSuccess);
    }

    private static LiveKitRoomAdminService CreateRoomAdmin(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:Url"] = "wss://warptalk-staging.livekit.cloud",
                ["LiveKit:ApiKey"] = "test-api-key",
                ["LiveKit:ApiSecret"] = "test-api-secret-with-at-least-32-characters",
            })
            .Build();
        return new LiveKitRoomAdminService(new HttpClient(handler), configuration, NullLogger<LiveKitRoomAdminService>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
