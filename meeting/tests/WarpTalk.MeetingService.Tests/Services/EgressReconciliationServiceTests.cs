using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// WT-371 #8. Recording had one completion path — LiveKit's <c>egress_ended</c> webhook — and on
/// production that webhook was never configured. The failure was silent in the worst way:
/// StartRoomCompositeEgress returned a real egress id so the host was told recording had begun,
/// LiveKit recorded and uploaded the file, and then nothing. Five rooms held an ActiveEgressId for
/// five days and not one artifact row was ever written.
///
/// These tests pin the two halves of the fix that matter. The sweep must FINISH a recording the
/// webhook never reported, and — the half that is easy to get wrong and dangerous to get wrong —
/// it must never clear a room whose recording is still running, or whose status it could not
/// actually determine.
/// </summary>
public sealed class EgressReconciliationServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FinishedEgressIsClearedAndPublished()
    {
        var room = Room(egressId: "EG_123");
        var (sut, unitOfWork, redis) = Build(
            room,
            Egress("EG_123", "EGRESS_COMPLETE", "s3://recordings/room-123.mp4"));

        var result = await sut.ReconcileAsync(Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Null(room.ActiveEgressId);
        redis.Verify(
            r => r.PublishStreamMessageAsync("meeting:domain-events", It.IsAny<Dictionary<string, string>>()),
            Times.Once);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunningEgressIsLeftAlone()
    {
        var room = Room(egressId: "EG_123");
        var (sut, unitOfWork, redis) = Build(room, Egress("EG_123", "EGRESS_ACTIVE", fileUrl: null));

        var result = await sut.ReconcileAsync(Now);

        Assert.Equal(0, result.Value);
        Assert.Equal("EG_123", room.ActiveEgressId);
        redis.VerifyNoOtherCalls();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailedEgressStopsTheRecordingWithoutInventingAnArtifact()
    {
        // Terminal, so the room is no longer recording and must stop saying it is — but there is
        // no file, so publishing a RecordingCompleted would promise a video that does not exist.
        var room = Room(egressId: "EG_123");
        var (sut, _, redis) = Build(room, Egress("EG_123", "EGRESS_FAILED", fileUrl: null));

        var result = await sut.ReconcileAsync(Now);

        Assert.Equal(1, result.Value);
        Assert.Null(room.ActiveEgressId);
        redis.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LiveKitBeingUnreachableNeverStopsARecording()
    {
        // The dangerous case. Treating "I could not ask" as "it finished" would tell a host their
        // live recording had stopped, mid-meeting, because of a transient network failure.
        //
        // updatedAt is deliberately PAST the unknown-egress grace window. A failed lookup carries
        // a null Value, so a version of this code without the IsSuccess guard would fall into the
        // "LiveKit has never heard of this id" branch instead — and inside the grace window that
        // branch also leaves the room alone, so this test passed with the guard deleted. It was
        // asserting the grace window, not the guard. Aged past the window, only the guard can
        // keep the room recording, which is the thing being tested.
        var room = Room(egressId: "EG_123", updatedAt: Now.AddHours(-3));
        var egressService = new Mock<ILiveKitEgressService>();
        egressService
            .Setup(s => s.GetEgressAsync("EG_123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<JsonElement?>("timeout", "LIVEKIT_EGRESS_LIST_FAILED"));
        var (sut, unitOfWork, redis) = Build(room, egressService);

        var result = await sut.ReconcileAsync(Now);

        Assert.Equal(0, result.Value);
        Assert.Equal("EG_123", room.ActiveEgressId);
        redis.VerifyNoOtherCalls();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnEgressLiveKitHasNotHeardOfYetIsGivenTimeToAppear()
    {
        // "Unknown" is ambiguous: either just-started and not yet visible, or long since aged out
        // of LiveKit's history. Inside the grace window only the first reading is possible.
        var room = Room(egressId: "EG_123", updatedAt: Now.AddMinutes(-5));
        var (sut, _, redis) = Build(room, unknownEgress: true);

        var result = await sut.ReconcileAsync(Now);

        Assert.Equal(0, result.Value);
        Assert.Equal("EG_123", room.ActiveEgressId);
        redis.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnEgressStillUnknownAfterTheGraceWindowStopsHoldingTheRoom()
    {
        // Past the window the only remaining reading is "aged out", so the room must stop claiming
        // to record — the exact state five production rooms sat in for five days. Nothing is
        // published: we never learned of a file.
        var room = Room(egressId: "EG_123", updatedAt: Now.AddHours(-3));
        var (sut, _, redis) = Build(room, unknownEgress: true);

        var result = await sut.ReconcileAsync(Now);

        Assert.Equal(1, result.Value);
        Assert.Null(room.ActiveEgressId);
        redis.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NothingRecordingMeansNoCallToLiveKitAtAll()
    {
        var egressService = new Mock<ILiveKitEgressService>(MockBehavior.Strict);
        var (sut, unitOfWork, _) = Build(room: null, egressService);

        var result = await sut.ReconcileAsync(Now);

        Assert.Equal(0, result.Value);
        egressService.VerifyNoOtherCalls();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OneUnreadableEgressDoesNotAbandonTheRestOfTheBatch()
    {
        var broken = Room(egressId: "EG_broken");
        var good = Room(egressId: "EG_good");
        var egressService = new Mock<ILiveKitEgressService>();
        egressService
            .Setup(s => s.GetEgressAsync("EG_broken", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        egressService
            .Setup(s => s.GetEgressAsync("EG_good", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<JsonElement?>(
                Egress("EG_good", "EGRESS_COMPLETE", "s3://recordings/good.mp4")));

        var (sut, _, redis) = Build(egressService, broken, good);

        var result = await sut.ReconcileAsync(Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Equal("EG_broken", broken.ActiveEgressId);
        Assert.Null(good.ActiveEgressId);
        redis.Verify(
            r => r.PublishStreamMessageAsync("meeting:domain-events", It.IsAny<Dictionary<string, string>>()),
            Times.Once);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────

    private static MeetingRoom Room(string egressId, DateTime? updatedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = Guid.NewGuid(),
        ProviderRoomName = $"room-{egressId}",
        ActiveEgressId = egressId,
        Status = "IN_PROGRESS",
        UpdatedAt = updatedAt ?? Now.AddMinutes(-30)
    };

    private static JsonElement Egress(string egressId, string status, string? fileUrl)
    {
        var files = fileUrl is null
            ? "[]"
            : $$"""[ { "location": "{{fileUrl}}", "size": 1024 } ]""";
        var json = $$"""
            { "egressId": "{{egressId}}", "status": "{{status}}", "fileResults": {{files}} }
            """;
        using var document = JsonDocument.Parse(json);
        // Cloned for the same reason the production lookup clones: the document is disposed here.
        return document.RootElement.Clone();
    }

    private static (EgressReconciliationService, Mock<IUnitOfWork>, Mock<IRedisService>) Build(
        MeetingRoom room,
        JsonElement egressInfo)
    {
        var egressService = new Mock<ILiveKitEgressService>();
        egressService
            .Setup(s => s.GetEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<JsonElement?>(egressInfo));
        return Build(room, egressService);
    }

    private static (EgressReconciliationService, Mock<IUnitOfWork>, Mock<IRedisService>) Build(
        MeetingRoom room,
        bool unknownEgress)
    {
        var egressService = new Mock<ILiveKitEgressService>();
        egressService
            .Setup(s => s.GetEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<JsonElement?>(null));
        return Build(room, egressService);
    }

    private static (EgressReconciliationService, Mock<IUnitOfWork>, Mock<IRedisService>) Build(
        MeetingRoom? room,
        Mock<ILiveKitEgressService> egressService)
        => Build(egressService, room is null ? Array.Empty<MeetingRoom>() : new[] { room });

    private static (EgressReconciliationService, Mock<IUnitOfWork>, Mock<IRedisService>) Build(
        Mock<ILiveKitEgressService> egressService,
        params MeetingRoom[] rooms)
    {
        var roomRepository = new Mock<IMeetingRoomRepository>();
        roomRepository
            .Setup(repository => repository.FindAsync(
                It.IsAny<Expression<Func<MeetingRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rooms);
        // The completion step re-finds the room by its egress id, exactly as the webhook does.
        roomRepository
            .Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                rooms.FirstOrDefault(predicate.Compile()));

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.MeetingRoomRepository).Returns(roomRepository.Object);
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var redis = new Mock<IRedisService>();
        redis
            .Setup(r => r.PublishStreamMessageAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(Result.Success(true));

        var sut = new EgressReconciliationService(
            unitOfWork.Object,
            egressService.Object,
            new EgressCompletion(unitOfWork.Object, redis.Object),
            NullLogger<EgressReconciliationService>.Instance);

        return (sut, unitOfWork, redis);
    }
}
