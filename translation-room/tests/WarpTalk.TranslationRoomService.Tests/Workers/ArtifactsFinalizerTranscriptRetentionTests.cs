using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;
using WarpTalk.TranslationRoomService.Infrastructure.BackgroundProcessors;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// WT-870 — a meeting held with "Save the meeting transcript" off still ended with a Summary on
/// its record, beside a transcript area that could not load. ai_assistant_worker had every live
/// line and wrote the summary into Redis; the finalizer read it and saved it without asking
/// whether the meeting was meant to have one.
///
/// These drive the whole finalization over a room whose setting is off and one whose setting is
/// on. The transcript client is left unanswered on purpose: the transcript half falls back to its
/// "could not be retrieved" artifact, which is not what is under test here.
/// </summary>
public sealed class ArtifactsFinalizerTranscriptRetentionTests
{
    private const string Structured =
        "{\"summary\":\"Planned the sprint.\",\"decisions\":[],\"actionItems\":[],\"templateKey\":\"general\",\"summaryLanguage\":\"en\"}";

    [Fact]
    public async Task AMeetingThatKeptNoTranscriptIsFinalizedWithoutASummary()
    {
        var harness = new Harness(saveTranscript: false);

        await harness.Finalizer.FinalizeRoomArtifactsAsync(harness.RoomId, ct: CancellationToken.None);

        Assert.DoesNotContain(harness.Added, artifact => artifact.ArtifactType == "SUMMARY_EXPORT");
        Assert.Contains(harness.Added, artifact => artifact.ArtifactType == "TRANSCRIPT_EXPORT");

        // Not waited for, not re-requested, not indexed — and whatever the worker left is dropped.
        harness.Redis.Verify(r => r.HashGetAsync(harness.SummaryKey, It.IsAny<string>()), Times.Never);
        harness.Redis.Verify(
            r => r.StreamAddAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
        harness.Redis.Verify(r => r.KeyDeleteAsync(harness.SummaryKey), Times.Once);
        harness.Knowledge.Verify(
            k => k.PublishAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The meeting still completes: finalization is not stuck waiting for a summary.
        harness.Events.Verify(
            e => e.ProcessEventAsync(harness.RoomId, null, "outputs_linked", "{}", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AMeetingThatKeptItsTranscriptStillGetsItsSummary()
    {
        var harness = new Harness(saveTranscript: true);

        await harness.Finalizer.FinalizeRoomArtifactsAsync(harness.RoomId, ct: CancellationToken.None);

        var summary = Assert.Single(harness.Added, artifact => artifact.ArtifactType == "SUMMARY_EXPORT");
        Assert.Equal(Structured, summary.Content);
        Assert.Contains(harness.Added, artifact => artifact.ArtifactType == "TRANSCRIPT_EXPORT");
    }

    [Fact]
    public async Task ARoomWhoseBlobPredatesTheSettingKeepsItsSummary()
    {
        // No save_transcript key at all: every room created before the setting existed.
        var harness = new Harness(saveTranscript: null);

        await harness.Finalizer.FinalizeRoomArtifactsAsync(harness.RoomId, ct: CancellationToken.None);

        Assert.Single(harness.Added, artifact => artifact.ArtifactType == "SUMMARY_EXPORT");
    }

    private sealed class Harness
    {
        public Guid RoomId { get; } = Guid.NewGuid();
        public string SummaryKey => $"meeting:{RoomId}:summary";
        public List<TranslationRoomArtifact> Added { get; } = new();
        public Mock<IRedisStateRepository> Redis { get; } = new();
        public Mock<IKnowledgeFactRequestPublisher> Knowledge { get; } = new();
        public Mock<IAudioRouteEventProcessor> Events { get; } = new();
        public ArtifactsFinalizer Finalizer { get; }

        public Harness(bool? saveTranscript)
        {
            var room = new TranslationRoom
            {
                Id = RoomId,
                WorkspaceId = Guid.NewGuid(),
                Title = "Sprint planning",
                Status = "ENDED",
                TargetLanguages = "[]",
                Settings = saveTranscript is { } save
                    ? JsonSerializer.Serialize(new TranslationRoomSettings { SaveTranscript = save })
                    : "{}"
            };

            var rooms = new Mock<ITranslationRoomRepository>();
            rooms.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);

            var artifacts = new Mock<ITranslationRoomArtifactRepository>();
            artifacts
                .Setup(r => r.AddAsync(It.IsAny<TranslationRoomArtifact>(), It.IsAny<CancellationToken>()))
                .Callback((TranslationRoomArtifact artifact, CancellationToken _) => Added.Add(artifact))
                .Returns(Task.CompletedTask);

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(rooms.Object);
            unitOfWork.SetupGet(u => u.TranslationRoomArtifactRepository).Returns(artifacts.Object);
            unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            Redis.Setup(r => r.HashGetAsync(SummaryKey, MeetingSummaryHash.StructuredJson)).ReturnsAsync(Structured);

            Events
                .Setup(e => e.ProcessEventAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            var cache = new Mock<ITranscriptCacheService>();
            cache.Setup(c => c.ReadCachedSegmentsAsync(It.IsAny<string>())).ReturnsAsync(Array.Empty<string>());

            Finalizer = new ArtifactsFinalizer(
                unitOfWork.Object,
                Redis.Object,
                Events.Object,
                NullLogger<ArtifactsFinalizer>.Instance,
                // Unanswered: the transcript half takes its unavailable path, which still yields
                // exactly one transcript artifact.
                new Mock<TranscriptService.TranscriptServiceClient>().Object,
                Options.Create(new ArtifactFinalizationSettings { MaxLocalRetries = 1 }),
                cache.Object,
                Knowledge.Object)
            {
                SummaryWaitTimeout = TimeSpan.FromMilliseconds(50),
                SummaryPollInterval = TimeSpan.FromMilliseconds(10)
            };
        }
    }
}
