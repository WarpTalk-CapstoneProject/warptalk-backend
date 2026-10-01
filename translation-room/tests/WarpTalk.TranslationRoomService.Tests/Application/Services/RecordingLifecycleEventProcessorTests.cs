using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared.Events;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// rec-loss: one recording row per egress, whatever order its lifecycle events arrive in.
///
/// The repository is a small in-memory stand-in rather than a set of per-call mocks, because the
/// cases worth pinning are SEQUENCES — completed before started, failed then completed — and a
/// sequence is only meaningful if the second event sees what the first one wrote.
/// </summary>
public sealed class RecordingLifecycleEventProcessorTests
{
    private const string EgressId = "EG_123";
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly DateTime StartedAt = new(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeArtifactStore _store = new();
    private readonly RecordingLifecycleEventProcessor _sut;

    public RecordingLifecycleEventProcessorTests()
    {
        _sut = new RecordingLifecycleEventProcessor(
            _store.UnitOfWork.Object,
            NullLogger<RecordingLifecycleEventProcessor>.Instance);
    }

    // ---- WT-826: auto-share counts as the release -------------------------------------------

    /// <summary>
    /// A recording LiveKit finishes uploading after the room ended — and after that room shared
    /// its record automatically — must not sit behind a hold the host already released by leaving
    /// auto-share on. The room's end released every recording row that existed then; this is the
    /// one that did not exist yet.
    /// </summary>
    [Fact]
    public async Task Completed_AfterTheRoomAutoSharedItsRecord_IsNotHeld()
    {
        GivenRoom("ENDED", "{\"artifact_access\":\"ALL_PARTICIPANTS\",\"auto_share_record\":true}");

        await _sut.ProcessAsync(Completed());

        Assert.False(Assert.Single(_store.Rows).ConsentRequired);
    }

    /// <summary>
    /// A room that ended before WT-826 carries no toggle, and nothing published it. Its recordings
    /// keep waiting for the host exactly as they always did — no retroactive release.
    /// </summary>
    [Fact]
    public async Task Completed_ForARoomThatEndedBeforeAutoShare_IsStillHeld()
    {
        GivenRoom("ENDED", "{\"artifact_access\":\"HOST_ONLY\"}");

        await _sut.ProcessAsync(Completed());

        Assert.True(Assert.Single(_store.Rows).ConsentRequired);
    }

    /// <summary>A room still running has not been published yet, whatever its toggle says.</summary>
    [Fact]
    public async Task Started_WhileTheMeetingIsStillRunning_IsHeld()
    {
        GivenRoom("IN_PROGRESS", "{\"artifact_access\":\"ALL_PARTICIPANTS\",\"auto_share_record\":true}");

        await _sut.ProcessAsync(Started());

        Assert.True(Assert.Single(_store.Rows).ConsentRequired);
    }

    private void GivenRoom(string status, string settings)
    {
        var rooms = new Mock<ITranslationRoomRepository>();
        rooms.Setup(repo => repo.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = RoomId, Status = status, Settings = settings });
        _store.UnitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(rooms.Object);
    }

    // ---- started ---------------------------------------------------------------------------

    [Fact]
    public async Task Started_WithNoRow_CreatesAProcessingRecording()
    {
        var envelope = Started();

        var result = await _sut.ProcessAsync(envelope);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        var row = Assert.Single(_store.Rows);
        Assert.Equal(RoomId, row.TranslationRoomId);
        Assert.Equal(EgressId, row.ProviderArtifactId);
        Assert.Equal("OPTIONAL_RECORDING", row.ArtifactType);
        Assert.Equal("PROCESSING", row.Status);
        Assert.Null(row.FileUrl);
        Assert.True(row.ContainsRawAudio);
        Assert.True(row.ContainsRawVideo);
        Assert.True(row.ConsentRequired);
        Assert.Equal(StartedAt, row.RecordingStartedAt);
        Assert.Equal(envelope.OccurredAt, row.CreatedAt);
        Assert.Equal(envelope.OccurredAt, row.UpdatedAt);
    }

    [Fact]
    public async Task Started_Twice_IsANoOp()
    {
        await _sut.ProcessAsync(Started());
        var savesBefore = _store.SaveCount;

        var result = await _sut.ProcessAsync(Started());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
        Assert.Equal("PROCESSING", Assert.Single(_store.Rows).Status);
        Assert.Equal(savesBefore, _store.SaveCount);
    }

    [Fact]
    public async Task Started_AfterCompleted_DoesNotReopenTheRecording()
    {
        await _sut.ProcessAsync(Completed());

        var result = await _sut.ProcessAsync(Started());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
        var row = Assert.Single(_store.Rows);
        Assert.Equal("COMPLETED", row.Status);
        Assert.Equal("s3://recordings/room.mp4", row.FileUrl);
    }

    [Fact]
    public async Task Started_AfterFailed_DoesNotReopenTheRecording()
    {
        await _sut.ProcessAsync(Failed());

        var result = await _sut.ProcessAsync(Started());

        Assert.False(result.Value);
        Assert.Equal("FAILED", Assert.Single(_store.Rows).Status);
    }

    // ---- completed -------------------------------------------------------------------------

    [Fact]
    public async Task Completed_WithNoRow_CreatesACompletedRecording()
    {
        var envelope = Completed();

        var result = await _sut.ProcessAsync(envelope);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        var row = Assert.Single(_store.Rows);
        Assert.Equal(EgressId, row.ProviderArtifactId);
        Assert.Equal("OPTIONAL_RECORDING", row.ArtifactType);
        Assert.Equal("COMPLETED", row.Status);
        Assert.Equal("s3://recordings/room.mp4", row.FileUrl);
        Assert.Equal("mp4", row.FileFormat);
        Assert.Equal(4096, row.FileSizeBytes);
        Assert.True(row.ContainsRawAudio);
        Assert.True(row.ContainsRawVideo);
        Assert.True(row.ConsentRequired);
    }

    [Fact]
    public async Task Completed_OnAProcessingRow_FillsTheFileAndKeepsTheStartTime()
    {
        var started = Started(occurredAt: StartedAt);
        await _sut.ProcessAsync(started);
        var completed = Completed(occurredAt: StartedAt.AddMinutes(30), startedAt: null);

        var result = await _sut.ProcessAsync(completed);

        Assert.True(result.Value);
        var row = Assert.Single(_store.Rows);
        Assert.Equal("COMPLETED", row.Status);
        Assert.Equal("s3://recordings/room.mp4", row.FileUrl);
        Assert.Equal("mp4", row.FileFormat);
        Assert.Equal(4096, row.FileSizeBytes);
        // The payload had no start; the one Started stored must survive.
        Assert.Equal(StartedAt, row.RecordingStartedAt);
        // The row is the SAME row: created when recording began, updated when it ended.
        Assert.Equal(started.OccurredAt, row.CreatedAt);
        Assert.Equal(completed.OccurredAt, row.UpdatedAt);
    }

    [Fact]
    public async Task Completed_Twice_IsIdempotent()
    {
        await _sut.ProcessAsync(Completed());
        var savesBefore = _store.SaveCount;

        var result = await _sut.ProcessAsync(Completed());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
        Assert.Single(_store.Rows);
        Assert.Equal(savesBefore, _store.SaveCount);
    }

    [Fact]
    public async Task Completed_BeforeStarted_EndsAsOneCompletedRow()
    {
        await _sut.ProcessAsync(Completed(startedAt: StartedAt));
        await _sut.ProcessAsync(Started());

        var row = Assert.Single(_store.Rows);
        Assert.Equal("COMPLETED", row.Status);
        Assert.Equal(StartedAt, row.RecordingStartedAt);
    }

    [Fact]
    public async Task Completed_AfterFailed_ARealFileWins()
    {
        await _sut.ProcessAsync(Started());
        await _sut.ProcessAsync(Failed());

        var result = await _sut.ProcessAsync(Completed());

        Assert.True(result.Value);
        var row = Assert.Single(_store.Rows);
        Assert.Equal("COMPLETED", row.Status);
        Assert.Equal("s3://recordings/room.mp4", row.FileUrl);
    }

    // ---- failed ----------------------------------------------------------------------------

    [Fact]
    public async Task Failed_WithNoRow_CreatesAFailedRecordingWithNoFile()
    {
        var result = await _sut.ProcessAsync(Failed());

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        var row = Assert.Single(_store.Rows);
        Assert.Equal(EgressId, row.ProviderArtifactId);
        Assert.Equal("OPTIONAL_RECORDING", row.ArtifactType);
        Assert.Equal("FAILED", row.Status);
        Assert.Null(row.FileUrl);
        Assert.Null(row.FileSizeBytes);
        // The host-safe reason has nowhere honest to live on this row, and LiveKit's raw error must
        // never be stored where a response could carry it.
        Assert.Null(row.Content);
    }

    [Fact]
    public async Task Failed_OnAProcessingRow_MarksItFailed()
    {
        await _sut.ProcessAsync(Started());
        var failed = Failed(occurredAt: StartedAt.AddMinutes(5));

        var result = await _sut.ProcessAsync(failed);

        Assert.True(result.Value);
        var row = Assert.Single(_store.Rows);
        Assert.Equal("FAILED", row.Status);
        Assert.Equal(failed.OccurredAt, row.UpdatedAt);
        Assert.Equal(StartedAt, row.RecordingStartedAt);
    }

    /// <summary>
    /// WT-824: the reason is kept on the row. The only two recordings production ever made both
    /// failed, LiveKit's egress runs in LiveKit Cloud where our logs cannot see it, and the reason
    /// lived only in a meeting-service log line that the next deploy deleted. A FAILED row with no
    /// reason is a dead end for the host and for whoever triages it.
    /// </summary>
    [Fact]
    public async Task Failed_KeepsTheReasonAndLiveKitsOwnStatusAndError()
    {
        await _sut.ProcessAsync(Failed());

        var row = Assert.Single(_store.Rows);
        Assert.NotNull(row.FailureReason);
        Assert.Contains("The recording stopped unexpectedly.", row.FailureReason);
        Assert.Contains("EGRESS_FAILED", row.FailureReason);
        Assert.Contains("upload to", row.FailureReason);
        // A storage endpoint is operator detail, and this row is readable by participants when
        // the host shares the meeting's outputs.
        Assert.DoesNotContain("https://bucket.example", row.FailureReason);
    }

    [Fact]
    public async Task Failed_OnAProcessingRow_KeepsTheReason()
    {
        await _sut.ProcessAsync(Started(occurredAt: StartedAt));
        await _sut.ProcessAsync(Failed(occurredAt: StartedAt.AddMinutes(4)));

        var row = Assert.Single(_store.Rows);
        Assert.Equal("FAILED", row.Status);
        Assert.Contains("EGRESS_FAILED", row.FailureReason);
    }

    [Fact]
    public async Task Completed_AfterFailed_ClearsTheReason()
    {
        await _sut.ProcessAsync(Failed());
        await _sut.ProcessAsync(Completed());

        var row = Assert.Single(_store.Rows);
        Assert.Equal("COMPLETED", row.Status);
        Assert.Null(row.FailureReason);
    }

    [Fact]
    public void FailureReason_IsBounded()
    {
        var reason = RecordingLifecycleEventProcessor.DescribeFailure(
            "The recording failed.", "EGRESS_FAILED", new string('x', 5000));

        Assert.True(reason.Length <= RecordingLifecycleEventProcessor.FailureReasonMaxLength);
    }

    [Fact]
    public async Task Failed_AfterCompleted_NeverDowngrades()
    {
        await _sut.ProcessAsync(Completed());
        var savesBefore = _store.SaveCount;

        var result = await _sut.ProcessAsync(Failed());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
        var row = Assert.Single(_store.Rows);
        Assert.Equal("COMPLETED", row.Status);
        Assert.Equal("s3://recordings/room.mp4", row.FileUrl);
        Assert.Equal(savesBefore, _store.SaveCount);
    }

    [Fact]
    public async Task Failed_Twice_IsANoOp()
    {
        await _sut.ProcessAsync(Failed());
        var savesBefore = _store.SaveCount;

        var result = await _sut.ProcessAsync(Failed());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
        Assert.Equal("FAILED", Assert.Single(_store.Rows).Status);
        Assert.Equal(savesBefore, _store.SaveCount);
    }

    // ---- races, deletion, validation -------------------------------------------------------

    [Fact]
    public async Task Completed_LosingTheCreateRace_AppliesItselfToTheWinnersRow()
    {
        // Another consumer inserts the Started row between our read and our insert; our insert
        // then hits the unique index on provider_artifact_id.
        _store.BeforeNextSave = () => _store.Rows.Add(new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            ProviderArtifactId = EgressId,
            ArtifactType = "OPTIONAL_RECORDING",
            Status = "PROCESSING",
            RecordingStartedAt = StartedAt,
            ConsentRequired = true,
        });

        var result = await _sut.ProcessAsync(Completed(startedAt: null));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        var row = Assert.Single(_store.Rows);
        Assert.Equal("COMPLETED", row.Status);
        Assert.Equal("s3://recordings/room.mp4", row.FileUrl);
        Assert.Equal(StartedAt, row.RecordingStartedAt);
        // Our losing insert must not still be pending, or the next save would retry it.
        Assert.Empty(_store.Pending);
    }

    [Fact]
    public async Task AnInsertFailureThatIsNotTheRace_IsRethrown()
    {
        _store.FailNextSaveWith = new DbUpdateException("foreign key violation");

        await Assert.ThrowsAsync<DbUpdateException>(() => _sut.ProcessAsync(Started()));
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task ADeletedRecording_IsNotBroughtBackByALateEvent()
    {
        await _sut.ProcessAsync(Started());
        _store.Rows[0].DeletedAt = StartedAt.AddDays(1);

        var result = await _sut.ProcessAsync(Completed());

        Assert.False(result.Value);
        Assert.Equal("PROCESSING", _store.Rows[0].Status);
    }

    [Fact]
    public async Task AnEnvelopeWithTheWrongEventType_IsRejected()
    {
        var envelope = Started() with { EventType = MeetingEventTypes.RecordingCompleted };

        var result = await _sut.ProcessAsync(envelope);

        Assert.False(result.IsSuccess);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task AFailedEventWithNoEgressId_IsRejected()
    {
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingFailed,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingFailedEventPayload(RoomId, " ", "Recording failed.", null, null));

        var result = await _sut.ProcessAsync(envelope);

        Assert.False(result.IsSuccess);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task AnEnvelopeWithNoPayload_IsRejectedRatherThanThrowing()
    {
        var envelope = Completed() with { Payload = null! };

        var result = await _sut.ProcessAsync(envelope);

        Assert.False(result.IsSuccess);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static EventEnvelope<MeetingRecordingStartedEventPayload> Started(DateTime? occurredAt = null) =>
        DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingStarted,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingStartedEventPayload(RoomId, EgressId, StartedAt))
        with { OccurredAt = occurredAt ?? StartedAt };

    private static EventEnvelope<MeetingRecordingCompletedEventPayload> Completed(
        DateTime? occurredAt = null,
        DateTime? startedAt = null) =>
        DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingCompleted,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingCompletedEventPayload(
                RoomId,
                EgressId,
                "s3://recordings/room.mp4",
                "mp4",
                4096,
                ContainsRawAudio: true,
                ContainsRawVideo: true,
                StartedAt: startedAt))
        with { OccurredAt = occurredAt ?? StartedAt.AddHours(1) };

    private static EventEnvelope<MeetingRecordingFailedEventPayload> Failed(DateTime? occurredAt = null) =>
        DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingFailed,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingFailedEventPayload(
                RoomId,
                EgressId,
                "The recording stopped unexpectedly.",
                "EGRESS_FAILED",
                "internal: upload to https://bucket.example failed"))
        with { OccurredAt = occurredAt ?? StartedAt.AddHours(1) };

    /// <summary>
    /// Tracks rows the way the scoped DbContext does for this processor: reads return the stored
    /// instance (so mutations are "tracked"), inserts are pending until SaveChanges, a pending
    /// insert whose provider id already exists fails like the unique index does, and Remove on a
    /// pending insert simply drops it.
    /// </summary>
    private sealed class FakeArtifactStore
    {
        public List<TranslationRoomArtifact> Rows { get; } = [];
        public List<TranslationRoomArtifact> Pending { get; } = [];
        public int SaveCount { get; private set; }
        public Action? BeforeNextSave { get; set; }
        public Exception? FailNextSaveWith { get; set; }
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        public FakeArtifactStore()
        {
            var repository = new Mock<ITranslationRoomArtifactRepository>();
            repository.Setup(repo => repo.FirstOrDefaultAsync(
                    It.IsAny<Expression<Func<TranslationRoomArtifact, bool>>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Expression<Func<TranslationRoomArtifact, bool>> predicate, string _, CancellationToken _) =>
                    Rows.FirstOrDefault(predicate.Compile()));
            repository.Setup(repo => repo.AddAsync(
                    It.IsAny<TranslationRoomArtifact>(),
                    It.IsAny<CancellationToken>()))
                .Callback<TranslationRoomArtifact, CancellationToken>((artifact, _) => Pending.Add(artifact))
                .Returns(Task.CompletedTask);
            repository.Setup(repo => repo.Remove(It.IsAny<TranslationRoomArtifact>()))
                .Callback<TranslationRoomArtifact>(artifact =>
                {
                    if (!Pending.Remove(artifact))
                        Rows.Remove(artifact);
                });

            UnitOfWork.SetupGet(work => work.TranslationRoomArtifactRepository)
                .Returns(repository.Object);
            UnitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var before = BeforeNextSave;
                    BeforeNextSave = null;
                    before?.Invoke();

                    if (FailNextSaveWith is { } failure)
                    {
                        FailNextSaveWith = null;
                        throw failure;
                    }

                    if (Pending.Any(pending => Rows.Any(row => row.ProviderArtifactId == pending.ProviderArtifactId)))
                        throw new DbUpdateException("duplicate key value violates unique constraint");

                    Rows.AddRange(Pending);
                    Pending.Clear();
                    SaveCount++;
                    return 1;
                });
        }
    }
}
