using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// The review finding on WT-701 (#423): a markdown fallback is stored, the finalizer keeps
/// <c>meeting:{id}:summary</c> so the structured summary can still land — and the late-summary
/// sweep, seeing a hash that held only <c>content</c>, rewrote the same fallback and DELETED the
/// key before structured_json arrived. These drive the real sweep over a Redis hash and an
/// artifact row.
/// </summary>
public sealed class ArtifactsReconciliationLateSummaryTests
{
    private const string Markdown = "## Meeting Summary\nWe shipped it.";

    private const string Structured =
        "{\"summary\":\"We shipped it.\",\"decisions\":[],\"actionItems\":[],"
        + "\"templateKey\":\"general\",\"summaryLanguage\":\"en\",\"insufficientData\":false}";

    [Fact]
    public void ContentAloneOverTheSameFallbackWaits() =>
        Assert.Equal(
            ArtifactsReconciliationWorker.LateSummaryDecision.Wait,
            ArtifactsReconciliationWorker.DecideLateSummary(
                SummaryContentBuilder.Build(null, Markdown, null),
                SummaryContentBuilder.Build(null, Markdown, null),
                structuredJson: null));

    [Fact]
    public void ContentAloneOverThePlaceholderUpgradesButKeepsWaiting() =>
        Assert.Equal(
            ArtifactsReconciliationWorker.LateSummaryDecision.UpgradeAndKeepWaiting,
            ArtifactsReconciliationWorker.DecideLateSummary(
                SummaryContentBuilder.Build(null, null, null),
                SummaryContentBuilder.Build(null, Markdown, null),
                structuredJson: null));

    [Fact]
    public void StructuredJsonUpgradesAndReleases() =>
        Assert.Equal(
            ArtifactsReconciliationWorker.LateSummaryDecision.UpgradeAndRelease,
            ArtifactsReconciliationWorker.DecideLateSummary(
                SummaryContentBuilder.Build(null, Markdown, null),
                Structured,
                Structured));

    /// <summary>The bug itself: a content-only hash over the stored fallback is left alone.</summary>
    [Fact]
    public async Task AFallbackIsNeitherRewrittenNorReleasedWhileOnlyContentIsThere()
    {
        var fallback = SummaryContentBuilder.Build(null, Markdown, null);
        var harness = new Harness(fallback);
        harness.Hash["content"] = Markdown;

        await harness.Worker.RecoverLateSummariesAsync(CancellationToken.None);

        Assert.Equal(fallback, harness.Artifact.Content);
        Assert.True(harness.KeyExists);
        Assert.Equal(0, harness.Saves);
        Assert.Equal(0, harness.Deletes);
    }

    /// <summary>
    /// End to end: fallback stored → structured_json appears → the next sweep upgrades the same
    /// row in place → the key is deleted exactly once, and later sweeps have nothing to do.
    /// </summary>
    [Fact]
    public async Task TheFallbackIsUpgradedInPlaceOnceStructuredJsonArrivesAndTheKeyIsDeletedOnce()
    {
        var fallback = SummaryContentBuilder.Build(null, Markdown, null);
        var harness = new Harness(fallback);
        var artifactId = harness.Artifact.Id;
        harness.Hash["content"] = Markdown;

        // Sweep 1: only content — waits.
        await harness.Worker.RecoverLateSummariesAsync(CancellationToken.None);
        Assert.True(harness.KeyExists);
        Assert.Equal(fallback, harness.Artifact.Content);

        // ai_assistant_worker finishes the structured summary.
        harness.Hash["structured_json"] = Structured;

        // Sweep 2: upgrades the same row and releases the key.
        await harness.Worker.RecoverLateSummariesAsync(CancellationToken.None);
        Assert.Equal(artifactId, harness.Artifact.Id);
        Assert.Equal(Structured, harness.Artifact.Content);
        Assert.Equal(1, harness.Saves);
        Assert.False(harness.KeyExists);
        Assert.Equal(1, harness.Deletes);

        // Sweep 3: nothing left to do.
        await harness.Worker.RecoverLateSummariesAsync(CancellationToken.None);
        Assert.Equal(1, harness.Saves);
        Assert.Equal(1, harness.Deletes);
    }

    [Fact]
    public async Task APlaceholderIsUpgradedToTheFallbackAndTheKeyIsKeptForTheStructuredSummary()
    {
        var harness = new Harness(SummaryContentBuilder.Build(null, null, null));
        harness.Hash["content"] = Markdown;

        await harness.Worker.RecoverLateSummariesAsync(CancellationToken.None);

        Assert.Equal(SummaryContentBuilder.Build(null, Markdown, null), harness.Artifact.Content);
        Assert.True(harness.KeyExists);
        Assert.Equal(0, harness.Deletes);

        // And the next sweep, with nothing new, leaves it be.
        await harness.Worker.RecoverLateSummariesAsync(CancellationToken.None);
        Assert.Equal(1, harness.Saves);
    }

    private sealed class Harness
    {
        public Dictionary<string, string> Hash { get; } = new();
        public bool KeyExists => Hash.Count > 0 && !_deleted;
        public int Deletes { get; private set; }
        public int Saves { get; private set; }
        public TranslationRoomArtifact Artifact { get; }
        public ArtifactsReconciliationWorker Worker { get; }

        private bool _deleted;

        public Harness(string storedContent)
        {
            var room = new TranslationRoom
            {
                Id = Guid.NewGuid(),
                Status = "ENDED",
                EndedAt = DateTime.UtcNow.AddMinutes(-15)
            };
            Artifact = new TranslationRoomArtifact
            {
                Id = Guid.NewGuid(),
                TranslationRoomId = room.Id,
                ArtifactType = "SUMMARY_EXPORT",
                Status = "COMPLETED",
                Content = storedContent,
                CreatedAt = DateTime.UtcNow.AddMinutes(-14)
            };
            room.TranslationRoomArtifacts.Add(Artifact);
            var summaryKey = $"meeting:{room.Id}:summary";

            var rooms = new Mock<ITranslationRoomRepository>();
            rooms
                .Setup(repo => repo.FindAsync(
                    It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoom, bool>>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TranslationRoom> { room });

            var artifacts = new Mock<ITranslationRoomArtifactRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(rooms.Object);
            unitOfWork.SetupGet(work => work.TranslationRoomArtifactRepository).Returns(artifacts.Object);
            unitOfWork
                .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Saves++)
                .ReturnsAsync(1);

            var db = new Mock<IDatabase>();
            db.Setup(item => item.KeyExistsAsync(summaryKey, It.IsAny<CommandFlags>()))
                .ReturnsAsync(() => KeyExists);
            db.Setup(item => item.HashGetAllAsync(summaryKey, It.IsAny<CommandFlags>()))
                .ReturnsAsync(() => KeyExists
                    ? Hash.Select(pair => new HashEntry(pair.Key, pair.Value)).ToArray()
                    : Array.Empty<HashEntry>());
            db.Setup(item => item.KeyDeleteAsync(summaryKey, It.IsAny<CommandFlags>()))
                .Callback(() => { Deletes++; _deleted = true; })
                .ReturnsAsync(true);

            var redis = new Mock<IConnectionMultiplexer>();
            redis.Setup(item => item.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);

            var services = new ServiceCollection();
            services.AddScoped(_ => unitOfWork.Object);

            Worker = new ArtifactsReconciliationWorker(
                services.BuildServiceProvider(),
                new Mock<IArtifactsFinalizationQueue>().Object,
                redis.Object,
                Options.Create(new ArtifactFinalizationSettings()),
                NullLogger<ArtifactsReconciliationWorker>.Instance,
                new WarpTalk.Shared.Coordination.DistributedLockProvider(new WarpTalk.Shared.Coordination.InProcessLeaseStore(TimeProvider.System), TimeProvider.System));
        }
    }
}
