using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// Switching a finished meeting's summary (and so its biên bản) into another of the meeting's
/// languages, against the shape production actually has.
///
/// The rooms here are the demo workspace's as they are stored in production on 2026-09-18:
/// source `en`, targets `["en","vi"]`, workspace whitelist `["vi","en"]`, a catalog keyed by
/// locale tags (`vi-VN`), and a published summary stamped general/en that already carries a
/// Vietnamese `translations.vi`. The policy is the REAL RoomArtifactLanguagePolicy, not a stub,
/// because "which languages can this meeting be read in" is the question under test.
/// </summary>
public sealed class SummaryLanguageSwitchTests
{
    private const string PublishedEnglish =
        "{\"summary\":\"The team reviewed the AI customer system.\","
        + "\"decisions\":[],"
        + "\"actionItems\":[{\"task\":\"Prepare the report\",\"owner\":\"Ky\",\"atMs\":97470}],"
        + "\"citations\":[{\"key\":\"summary\",\"atMs\":1200}],"
        + "\"templateKey\":\"general\",\"summaryLanguage\":\"en\",\"insufficientData\":false,"
        + "\"translations\":{\"vi\":{\"summary\":\"Nhóm đã xem xét hệ thống AI.\","
        + "\"actionItems\":[{\"task\":\"Chuẩn bị báo cáo\",\"owner\":\"Ky\",\"atMs\":97470}]}}}";

    [Fact]
    public async Task AMeetingLanguageIsQueuedAsATranslationOfThePublishedSummary()
    {
        var (room, reader) = ProductionShapedRoom(PublishedEnglish);
        var redis = new InMemoryRedisState();
        var service = CreateService(room, redis);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "vi", "Bearer t");

        Assert.True(result.IsSuccess, $"{result.ErrorCode}: {result.Error}");
        Assert.Equal(SummaryVariantStatus.Generating, result.Value!.Status);

        var queued = Assert.Single(redis.Stream);
        Assert.Equal(SummaryDelivery.Variant, queued["delivery"]);
        Assert.Equal("vi", queued["summary_language"]);
        Assert.Equal("general", queued["template_key"]);
        // Translated FROM the published summary — the one that already carries translations.vi —
        // rather than written again from the transcript.
        Assert.Equal(SummaryRequestMode.Translate, queued["mode"]);
        Assert.Equal(PublishedEnglish, queued["source_content_json"]);
    }

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("vi_VN")]
    [InlineData("VI")]
    public async Task EverySpellingOfAMeetingLanguageIsTheSameRequest(string spelling)
    {
        var (room, reader) = ProductionShapedRoom(PublishedEnglish);
        var redis = new InMemoryRedisState();
        var service = CreateService(room, redis);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", spelling, "Bearer t");

        Assert.True(result.IsSuccess, $"{result.ErrorCode}: {result.Error}");
        Assert.Equal("vi", result.Value!.Language);
        Assert.Equal("vi", Assert.Single(redis.Stream)["summary_language"]);
    }

    [Fact]
    public async Task AnotherShapeIsStillWrittenFromTheTranscript()
    {
        var (room, reader) = ProductionShapedRoom(PublishedEnglish);
        var redis = new InMemoryRedisState();
        var service = CreateService(room, redis);

        await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "standup", "vi", "Bearer t");

        var queued = Assert.Single(redis.Stream);
        Assert.Equal(SummaryRequestMode.Generate, queued["mode"]);
        Assert.Equal(string.Empty, queued["source_content_json"]);
    }

    [Fact]
    public async Task APlaceholderSummaryIsNeverTranslated()
    {
        const string placeholder =
            "{\"summary\":\"The AI assistant could not generate a summary for this meeting.\","
            + "\"decisions\":[],\"actionItems\":[],\"insufficientData\":true}";
        var (room, reader) = ProductionShapedRoom(placeholder);
        var redis = new InMemoryRedisState();
        var service = CreateService(room, redis);

        await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "vi", "Bearer t");

        Assert.Equal(SummaryRequestMode.Generate, Assert.Single(redis.Stream)["mode"]);
    }

    /// <summary>
    /// WT-703 still holds: a language outside the meeting's own is refused before anything is
    /// claimed or queued. This is what the web's seven-language picker ran into for five of its
    /// options on every en→vi meeting.
    /// </summary>
    [Fact]
    public async Task ALanguageOutsideTheMeetingIsRefusedWithTheAllowedList()
    {
        var (room, reader) = ProductionShapedRoom(PublishedEnglish);
        var redis = new InMemoryRedisState();
        var service = CreateService(room, redis);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "ja", "Bearer t");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains("en", result.Error);
        Assert.Contains("vi", result.Error);
        Assert.Empty(redis.Stream);
        Assert.Empty(redis.Strings);
    }

    /// <summary>
    /// REVIEW FINDING ON #423 — two pollers, one completed run, and unconditional deletes.
    ///
    /// Both polls read the in-flight claim R1 and its `completed` outcome. The first releases R1,
    /// claims R2 and queues the retry. The second, still holding its stale read of R1, used to
    /// DELETE the claim outright — removing R2 — and then win SET NX with R3, queueing a second
    /// retry beside the first. With compare-and-delete its release is a no-op, its claim loses,
    /// and exactly one retry runs.
    /// </summary>
    [Fact]
    public async Task TwoPollersThatBothSawTheRunCompleteQueueOneRetryNotTwo()
    {
        var (room, reader) = ProductionShapedRoom(PublishedEnglish);
        var redis = new InMemoryRedisState();
        var service = CreateService(room, redis);

        var inFlightKey = TranslationRoomConstants.SummaryVariantInFlightKeyPrefix + $"{room.Id}:general:vi";
        const string firstRun = "run-1";
        redis.Strings[inFlightKey] = firstRun;
        redis.Strings[TranslationRoomConstants.SummaryRewriteStatusKeyPrefix + firstRun] =
            "{\"Status\":\"completed\",\"Error\":null}";

        // Poll A runs to completion: releases run-1, claims a retry, queues it.
        var first = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "vi", "Bearer t");
        Assert.Equal(SummaryVariantStatus.Generating, first.Value!.Status);
        var retryClaim = redis.Strings[inFlightKey];
        Assert.NotEqual(firstRun, retryClaim);
        Assert.Single(redis.Stream);

        // Poll B read the same state BEFORE poll A released it — replay that stale read.
        redis.StaleReads[inFlightKey] = firstRun;
        redis.StaleReads[TranslationRoomConstants.SummaryRewriteStatusKeyPrefix + firstRun] =
            "{\"Status\":\"completed\",\"Error\":null}";

        var second = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "vi", "Bearer t");

        Assert.Equal(SummaryVariantStatus.Generating, second.Value!.Status);
        // A's claim survived B's release, and nothing was queued a second time.
        Assert.Equal(retryClaim, redis.Strings[inFlightKey]);
        Assert.Single(redis.Stream);
    }

    [Theory]
    [InlineData("general", "vi", true)]
    [InlineData("GENERAL", "vi", true)]
    [InlineData("standup", "vi", false)]
    [InlineData("general", "", false)]
    public void OnlyASameShapeLanguageSwitchTranslates(string template, string language, bool translates)
    {
        var source = TranslationRoomArtifactService.TranslatableSource(
            PublishedEnglish, template.Trim().ToLowerInvariant(), language);

        Assert.Equal(translates, source != null);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"summary\":\"## Meeting Summary\",\"decisions\":[],\"actionItems\":[],\"insufficientData\":false}")]
    [InlineData("{\"summary\":\"x\",\"templateKey\":\"general\",\"generationFailed\":true}")]
    public void ContentWithNothingToCarryAcrossIsNotTranslated(string content) =>
        Assert.Null(TranslationRoomArtifactService.TranslatableSource(content, "general", "vi"));

    private static (TranslationRoom Room, Guid Reader) ProductionShapedRoom(string summaryContent)
    {
        var reader = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            Status = "ENDED",
            Settings = "{\"artifact_access\":\"ALL_PARTICIPANTS\"}",
            SourceLanguage = "en",
            TargetLanguages = "[\"en\", \"vi\"]"
        };
        room.TranslationRoomParticipants.Add(new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            UserId = reader
        });
        room.TranslationRoomArtifacts.Add(new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            ArtifactType = "SUMMARY_EXPORT",
            Status = "COMPLETED",
            Content = summaryContent,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        });
        return (room, reader);
    }

    private static TranslationRoomArtifactService CreateService(TranslationRoom room, InMemoryRedisState redis)
    {
        var rooms = new Mock<ITranslationRoomRepository>();
        rooms
            .Setup(repo => repo.FirstOrDefaultAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        var variants = new Mock<ITranslationRoomSummaryVariantRepository>();

        // The production catalog, locale-tagged exactly as seeded.
        var languages = new Mock<ILanguageRepository>();
        languages
            .Setup(repo => repo.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "en-US", "es-ES", "fr-FR", "ja-JP", "ko-KR", "vi-VN", "zh-CN" }
                .Select(code => new SupportedLanguage { Code = code, Name = code, IsActive = true })
                .ToList());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(rooms.Object);
        unitOfWork.SetupGet(work => work.TranslationRoomSummaryVariantRepository).Returns(variants.Object);
        unitOfWork.SetupGet(work => work.LanguageRepository).Returns(languages.Object);

        // The demo workspace's whitelist, as stored: AllowedTargetLanguages ["vi","en"].
        var workspacePolicy = new Mock<IWorkspaceMeetingPolicy>();
        workspacePolicy
            .Setup(policy => policy.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<string>>(new[] { "vi", "en" }));

        var policy = new RoomArtifactLanguagePolicy(
            unitOfWork.Object,
            workspacePolicy.Object,
            NullLogger<RoomArtifactLanguagePolicy>.Instance);

        return new TranslationRoomArtifactService(
            unitOfWork.Object,
            NullLogger<TranslationRoomArtifactService>.Instance,
            new Mock<IArtifactUrlSigner>().Object,
            redis,
            new Mock<IArtifactsFinalizationQueue>().Object,
            policy);
    }
}

/// <summary>
/// Just enough of Redis's string and stream semantics to exercise claims honestly: SET NX loses
/// when the key is held, and compare-and-delete only deletes a value that still matches.
/// <see cref="StaleReads"/> replays a value once, for simulating a read that happened before
/// another caller's write.
/// </summary>
internal sealed class InMemoryRedisState : IRedisStateRepository
{
    public Dictionary<string, string> Strings { get; } = new();

    public List<Dictionary<string, string>> Stream { get; } = new();

    public Dictionary<string, string> StaleReads { get; } = new();

    public Task<string?> StringGetAsync(string key)
    {
        if (StaleReads.Remove(key, out var stale)) return Task.FromResult<string?>(stale);
        return Task.FromResult(Strings.TryGetValue(key, out var value) ? value : null);
    }

    public Task<bool> StringSetIfAbsentAsync(string key, string value, TimeSpan expiry)
    {
        if (Strings.ContainsKey(key)) return Task.FromResult(false);
        Strings[key] = value;
        return Task.FromResult(true);
    }

    public Task<bool> StringSetAsync(string key, string value, TimeSpan? expiry = null)
    {
        Strings[key] = value;
        return Task.FromResult(true);
    }

    public Task<bool> KeyDeleteAsync(string key) => Task.FromResult(Strings.Remove(key));

    public Task<bool> KeyDeleteIfEqualsAsync(string key, string expectedValue)
    {
        if (Strings.TryGetValue(key, out var value) && value == expectedValue)
        {
            Strings.Remove(key);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<string> StreamAddAsync(string stream, Dictionary<string, string> fields)
    {
        Stream.Add(new Dictionary<string, string>(fields));
        return Task.FromResult($"{Stream.Count}-0");
    }

    public Task<Dictionary<string, string>> GetHashAllAsync(string key) => Task.FromResult(new Dictionary<string, string>());
    public Task HashSetAsync(string key, Dictionary<string, string> fields) => Task.CompletedTask;
    public Task<bool> KeyExpireAsync(string key, TimeSpan expiry) => Task.FromResult(true);
    public Task<string?> HashGetAsync(string key, string field) => Task.FromResult<string?>(null);
    public Task<bool> WaitForSignalAsync(string channel, TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
    public Task<long> PublishAsync(string channel, string message) => Task.FromResult(0L);
}
