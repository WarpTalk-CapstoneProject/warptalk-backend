using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// The counts here are the whole feature. A reader who picks English is told how much of the
/// meeting is actually readable in English, and everything the backfill does is decided by which
/// segments land in "missing" — so these tests are about which lines count, not about Redis.
/// </summary>
public class TranscriptTranslationBackfillServiceTests
{
    private static readonly Guid TranscriptId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RoomId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid WorkspaceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("vi_VN", "vi")]
    [InlineData("JA", "ja")]
    [InlineData("  ko  ", "ko")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeLanguage_ReducesLocaleTagsToTheBareCodeSegmentsAreStoredWith(string? input, string expected)
    {
        Assert.Equal(expected, TranscriptTranslationBackfillService.NormalizeLanguage(input));
    }

    [Fact]
    public void NormalizeLanguage_LetsALocaleTagMatchTheLanguageItIs()
    {
        // A room hands out "vi-VN"; STT writes "vi". Comparing the raw strings reports every
        // Vietnamese line as missing Vietnamese, and the backfill would then pay to translate
        // Vietnamese into Vietnamese.
        Assert.Equal(
            TranscriptTranslationBackfillService.NormalizeLanguage("vi"),
            TranscriptTranslationBackfillService.NormalizeLanguage("vi-VN"));
    }

    [Fact]
    public void IsTranslatableSegment_SkipsControlMarkersAndSystemLines()
    {
        Assert.False(TranscriptTranslationBackfillService.IsTranslatableSegment(Segment("__MEETING_END__", "vi")));
        Assert.False(TranscriptTranslationBackfillService.IsTranslatableSegment(Segment("  ", "vi")));
        Assert.False(TranscriptTranslationBackfillService.IsTranslatableSegment(Segment("joined the room", "system")));
        Assert.True(TranscriptTranslationBackfillService.IsTranslatableSegment(Segment("xin chào", "vi")));
    }

    [Fact]
    public async Task GetCoverage_CountsSpokenAndTranslatedSeparatelyAndLeavesTheRestMissing()
    {
        var vietnamese1 = Segment("một", "vi");
        var vietnamese2 = Segment("hai", "vi");
        var english = Segment("three", "en");
        var marker = Segment("__MEETING_END__", "vi");
        var system = Segment("Nhi joined", "system");

        var service = Build(
            [vietnamese1, vietnamese2, english, marker, system],
            [Link(vietnamese1.Id, "en")],
            out _);

        var result = await service.GetCoverageAsync(TranscriptId, UserId, "en-US");

        Assert.True(result.IsSuccess);
        var coverage = result.Value!;
        Assert.Equal("en", coverage.TargetLanguage);
        // The marker and the system line are not part of the meeting anyone reads.
        Assert.Equal(3, coverage.TotalSegments);
        Assert.Equal(1, coverage.SpokenInTarget);
        Assert.Equal(1, coverage.Translated);
        Assert.Equal(1, coverage.Missing);
        Assert.Equal(TranscriptTranslationBackfillService.StatusRunning, coverage.Status);
    }

    [Fact]
    public async Task GetCoverage_ReportsCompleteWhenNothingIsLeftEvenWhileAMarkerIsStillAlive()
    {
        // A run's marker outlives its last batch by up to its TTL. Trusting the marker over the
        // counts would leave the reader watching a progress bar that is already full.
        var english = Segment("three", "en");
        var vietnamese = Segment("một", "vi");

        var service = Build([english, vietnamese], [Link(vietnamese.Id, "en")], out _);

        var result = await service.GetCoverageAsync(TranscriptId, UserId, "en");

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.Missing);
        Assert.Equal(TranscriptTranslationBackfillService.StatusComplete, result.Value.Status);
    }

    [Fact]
    public async Task GetCoverage_IgnoresASupersededLink()
    {
        // A re-translation flips the old link's IsCurrent off. Counting it would report a line as
        // covered by text nothing serves any more.
        var vietnamese = Segment("một", "vi");
        var stale = Link(vietnamese.Id, "en");
        stale.IsCurrent = false;

        var service = Build([vietnamese], [stale], out _);

        var result = await service.GetCoverageAsync(TranscriptId, UserId, "en");

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.Translated);
        Assert.Equal(1, result.Value.Missing);
    }

    [Fact]
    public async Task RequestBackfill_QueuesEveryMissingSegmentAndNothingElse()
    {
        var missing = Enumerable.Range(0, TranscriptTranslationBackfillService.SegmentsPerRequest + 3)
            .Select(i => Segment($"câu {i}", "vi"))
            .ToList();
        var alreadyEnglish = Segment("already english", "en");

        var service = Build([.. missing, alreadyEnglish], [], out var database);
        database
            .StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(true);

        var result = await service.RequestBackfillAsync(TranscriptId, UserId, "en");

        Assert.True(result.IsSuccess);
        Assert.Equal(missing.Count, result.Value!.Missing);

        var calls = database.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDatabase.StreamAddAsync))
            .ToList();
        Assert.Equal(2, calls.Count); // 23 segments over a batch size of 20

        var queuedIds = calls
            .SelectMany(c => (NameValueEntry[])c.GetArguments()[1]!)
            .Where(e => e.Name == "segments_json")
            .SelectMany(e => System.Text.Json.JsonDocument.Parse(e.Value.ToString()).RootElement.EnumerateArray())
            .Select(e => e.GetProperty("segment_id").GetString())
            .ToList();

        Assert.Equal(missing.Count, queuedIds.Count);
        Assert.DoesNotContain(alreadyEnglish.Id.ToString(), queuedIds);

        var streams = calls.Select(c => ((RedisKey)c.GetArguments()[0]!).ToString()).Distinct().ToList();
        Assert.Equal([TranscriptTranslationBackfillService.RequestStream], streams);
    }

    [Fact]
    public async Task RequestBackfill_DoesNothingWhenARunIsAlreadyInFlight()
    {
        var vietnamese = Segment("một", "vi");
        var service = Build([vietnamese], [], out var database);

        // SET NX losing the race is the normal outcome when two readers open the same transcript
        // in the same language; queueing the same lines twice would pay for them twice.
        database
            .StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(false);

        var result = await service.RequestBackfillAsync(TranscriptId, UserId, "en");

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(
            database.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IDatabase.StreamAddAsync));
    }

    [Fact]
    public async Task RequestBackfill_StartsAgainOverTheCorpseOfAFailedRun()
    {
        // A run that failed leaves its marker behind for the rest of a 20 minute TTL. Treating
        // that as "already running" puts a Try again button in front of the reader that silently
        // does nothing for the rest of the window.
        var vietnamese = Segment("một", "vi");
        var service = Build([vietnamese], [], out var database);

        database
            .StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(false);
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(new RedisValue(TranscriptTranslationBackfillService.StatusFailed));

        var result = await service.RequestBackfillAsync(TranscriptId, UserId, "en");

        Assert.True(result.IsSuccess);
        Assert.Contains(
            database.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IDatabase.StreamAddAsync));
    }

    [Fact]
    public async Task RequestBackfill_RefusesAnEmptyLanguageRatherThanQueueingAgainstAnEmptyKey()
    {
        var service = Build([Segment("một", "vi")], [], out _);

        var result = await service.RequestBackfillAsync(TranscriptId, UserId, "   ");

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
    }

    [Fact]
    public async Task GetCoverage_RefusesAReaderWithoutAccess()
    {
        var service = Build([Segment("một", "vi")], [], out _, canRead: false);

        var result = await service.GetCoverageAsync(TranscriptId, UserId, "en");

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    private static TranscriptSegment Segment(string text, string language) => new()
    {
        Id = Guid.NewGuid(),
        TranscriptId = TranscriptId,
        OriginalText = text,
        OriginalLanguage = language,
        SequenceOrder = 0,
    };

    private static SegmentTranslationLink Link(Guid segmentId, string language) => new()
    {
        SegmentId = segmentId,
        TranslationContentId = Guid.NewGuid(),
        TargetLanguage = language,
        IsCurrent = true,
    };

    private static TranscriptTranslationBackfillService Build(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<SegmentTranslationLink> links,
        out IDatabase database,
        bool canRead = true)
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();

        var transcripts = Substitute.For<ITranscriptRepository>();
        transcripts.GetByIdAsync(TranscriptId, Arg.Any<CancellationToken>())
            .Returns(new Transcript
            {
                Id = TranscriptId,
                TranslationRoomId = RoomId,
                WorkspaceId = WorkspaceId,
            });
        unitOfWork.Transcripts.Returns(transcripts);

        var segmentRepository = Substitute.For<ITranscriptSegmentRepository>();
        segmentRepository
            .FindAsync(Arg.Any<Expression<Func<TranscriptSegment, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                segments.Where(call.Arg<Expression<Func<TranscriptSegment, bool>>>().Compile())));
        unitOfWork.TranscriptSegments.Returns(segmentRepository);

        var linkRepository = Substitute.For<ISegmentTranslationLinkRepository>();
        linkRepository
            .FindAsync(Arg.Any<Expression<Func<SegmentTranslationLink, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                links.Where(call.Arg<Expression<Func<SegmentTranslationLink, bool>>>().Compile())));
        unitOfWork.SegmentTranslationLinks.Returns(linkRepository);

        var readAccess = Substitute.For<ITranscriptReadAccess>();
        readAccess.CanReadRoomTranscriptAsync(RoomId, UserId, Arg.Any<CancellationToken>()).Returns(canRead);

        database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(new RedisValue(TranscriptTranslationBackfillService.StatusRunning));
        database.StreamAddAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<NameValueEntry[]>(),
                Arg.Any<RedisValue?>(),
                Arg.Any<int?>(),
                Arg.Any<bool>(),
                Arg.Any<CommandFlags>())
            .Returns(new RedisValue("1-0"));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        return new TranscriptTranslationBackfillService(
            unitOfWork,
            readAccess,
            redis,
            NullLogger<TranscriptTranslationBackfillService>.Instance);
    }
}
