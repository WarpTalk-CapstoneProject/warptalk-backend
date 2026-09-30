using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// WT-870, the write side of a rewrite: a summary result for a meeting that kept no transcript is
/// not stored, whatever queued it — and the person waiting on it is told why.
/// </summary>
public sealed class SummaryResultConsumerTranscriptRetentionTests
{
    private const string Structured =
        "{\"summary\":\"Planned the sprint.\",\"decisions\":[],\"actionItems\":[],\"templateKey\":\"general\",\"summaryLanguage\":\"\"}";

    [Fact]
    public async Task AResultForAMeetingThatKeptNoTranscriptIsNotStored()
    {
        var harness = new Harness(saveTranscript: false);

        await harness.Worker.ProcessEntryAsync(harness.Entry("canonical"), CancellationToken.None);

        Assert.Equal("old", harness.Summary.Content);
        harness.Artifacts.Verify(r => r.Update(It.IsAny<TranslationRoomArtifact>()), Times.Never);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("failed", harness.PublishedOutcome);
        Assert.Contains("without saving its transcript", harness.PublishedOutcome);
    }

    [Fact]
    public async Task ARenderingForAMeetingThatKeptNoTranscriptIsNotStoredEither()
    {
        var harness = new Harness(saveTranscript: false);

        await harness.Worker.ProcessEntryAsync(harness.Entry("variant"), CancellationToken.None);

        harness.Variants.Verify(
            r => r.AddAsync(It.IsAny<TranslationRoomSummaryVariant>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AResultForAMeetingThatKeptItsTranscriptStillReplacesTheSummary()
    {
        var harness = new Harness(saveTranscript: true);

        await harness.Worker.ProcessEntryAsync(harness.Entry("canonical"), CancellationToken.None);

        Assert.Equal(Structured, harness.Summary.Content);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("completed", harness.PublishedOutcome);
    }

    private sealed class Harness
    {
        private readonly Guid _roomId = Guid.NewGuid();

        public TranslationRoomArtifact Summary { get; }
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();
        public Mock<ITranslationRoomArtifactRepository> Artifacts { get; } = new();
        public Mock<ITranslationRoomSummaryVariantRepository> Variants { get; } = new();
        private readonly Mock<IDatabase> _db = new();

        /// <summary>
        /// Whatever PublishOutcomeAsync stored, read off the recorded call rather than a setup for
        /// one StringSetAsync overload — the client library has several and adds more.
        /// </summary>
        public string PublishedOutcome => string.Join(
            " | ",
            _db.Invocations
                .Where(call => call.Method.Name == nameof(IDatabase.StringSetAsync))
                .Select(call => call.Arguments[1]?.ToString()));

        public SummaryResultConsumerWorker Worker { get; }

        public Harness(bool saveTranscript)
        {
            var room = new TranslationRoom
            {
                Id = _roomId,
                WorkspaceId = Guid.Empty,
                Status = "ENDED",
                Settings = JsonSerializer.Serialize(new TranslationRoomSettings { SaveTranscript = saveTranscript })
            };

            Summary = new TranslationRoomArtifact
            {
                Id = Guid.NewGuid(),
                TranslationRoomId = _roomId,
                ArtifactType = "SUMMARY_EXPORT",
                Status = "COMPLETED",
                Content = "old",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            };

            var rooms = new Mock<ITranslationRoomRepository>();
            rooms.Setup(r => r.GetByIdAsync(_roomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);

            Artifacts
                .Setup(r => r.GetArtifactsByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TranslationRoomArtifact> { Summary });

            UnitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(rooms.Object);
            UnitOfWork.SetupGet(u => u.TranslationRoomArtifactRepository).Returns(Artifacts.Object);
            UnitOfWork.SetupGet(u => u.TranslationRoomSummaryVariantRepository).Returns(Variants.Object);
            UnitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var redis = new Mock<IConnectionMultiplexer>();
            redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_db.Object);

            var services = new ServiceCollection();
            services.AddScoped(_ => UnitOfWork.Object);
            services.AddScoped(_ => new Mock<WarpTalk.TranslationRoomService.Application.Interfaces.IKnowledgeFactRequestPublisher>().Object);

            Worker = new SummaryResultConsumerWorker(
                redis.Object,
                services.BuildServiceProvider(),
                NullLogger<SummaryResultConsumerWorker>.Instance);
        }

        public StreamEntry Entry(string delivery) => new(
            "1-0",
            new[]
            {
                new NameValueEntry("room_id", _roomId.ToString()),
                new NameValueEntry("request_id", "req-1"),
                new NameValueEntry("status", "completed"),
                new NameValueEntry("content_json", Structured),
                new NameValueEntry("delivery", delivery)
            });
    }
}
