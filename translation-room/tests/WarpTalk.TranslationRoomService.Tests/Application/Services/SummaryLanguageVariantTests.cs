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
/// Reading a meeting in your own language must not take it away from anybody else.
///
/// The bug these pin: the room's summary is ONE artifact row, rewriting REPLACES it, and the
/// rewrite gate admits every participant. So a Japanese attendee who asked to read the meeting
/// in Japanese destroyed the English summary the host had published — for everyone — and two
/// people reading different languages could take it from each other indefinitely.
/// </summary>
public sealed class SummaryLanguageVariantTests
{
    /// <summary>
    /// The load-bearing assertion of the whole feature: a reader's request publishes a request
    /// marked `variant`, which is what stops SummaryResultConsumerWorker overwriting the
    /// artifact when the answer comes back.
    /// </summary>
    [Fact]
    public async Task AReadersLanguageChoiceIsQueuedAsAVariantAndNeverAsARewrite()
    {
        var reader = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), reader, templateKey: "general", summaryLanguage: "");
        var redis = new Mock<IRedisStateRepository>();
        var service = CreateService(room, redis, variant: null);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "ja", "Bearer t");

        Assert.True(result.IsSuccess, $"{result.ErrorCode}: {result.Error}");
        Assert.Equal(SummaryVariantStatus.Generating, result.Value!.Status);
        Assert.Null(result.Value.Content);
        Assert.False(result.Value.IsCanonical);

        redis.Verify(
            r => r.StreamAddAsync(
                "assistant:summary_requests",
                It.Is<Dictionary<string, string>>(fields =>
                    fields["delivery"] == SummaryDelivery.Variant
                    && fields["summary_language"] == "ja"
                    && fields["template_key"] == "general")),
            Times.Once);
    }

    /// <summary>
    /// The pair the host published is served from the artifact itself. Copying it into the cache
    /// would create a second row that silently stops tracking the host's rewrites.
    /// </summary>
    [Fact]
    public async Task ThePublishedPairIsServedFromTheArtifactAndCostsNothing()
    {
        var reader = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), reader, templateKey: "general", summaryLanguage: "");
        var redis = new Mock<IRedisStateRepository>();
        var service = CreateService(room, redis, variant: null);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", null, "Bearer t");

        Assert.True(result.IsSuccess);
        Assert.Equal(SummaryVariantStatus.Ready, result.Value!.Status);
        Assert.True(result.Value.IsCanonical);
        Assert.NotNull(result.Value.Content);
        redis.VerifyNoOtherCalls();
    }

    /// <summary>
    /// "Published" is read off the artifact's OWN stamped pair, not assumed to be
    /// general/as-spoken. A host who rewrote the meeting into Standup made Standup the published
    /// shape, and asking for General then has to be a variant rather than a second reading of
    /// the same row.
    /// </summary>
    [Fact]
    public async Task WhatCountsAsPublishedFollowsTheHostsOwnRewrite()
    {
        var reader = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), reader, templateKey: "standup", summaryLanguage: "en");
        var redis = new Mock<IRedisStateRepository>();
        var service = CreateService(room, redis, variant: null);

        var published = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "standup", "en", "Bearer t");
        Assert.True(published.Value!.IsCanonical);
        Assert.Equal(SummaryVariantStatus.Ready, published.Value.Status);

        var other = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "en", "Bearer t");
        Assert.False(other.Value!.IsCanonical);
        Assert.Equal(SummaryVariantStatus.Generating, other.Value.Status);
    }

    /// <summary>
    /// The second reader of a language is the one this cache exists for: they pay no LLM call,
    /// and no request is published at all.
    /// </summary>
    [Fact]
    public async Task ASecondReaderOfTheSameLanguageQueuesNothing()
    {
        var reader = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), reader, templateKey: "general", summaryLanguage: "");
        var cached = new TranslationRoomSummaryVariant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = room.Id,
            TemplateKey = "general",
            Language = "ja",
            Content = "{\"summary\":\"日本語\",\"templateKey\":\"general\",\"summaryLanguage\":\"ja\"}",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        var redis = new Mock<IRedisStateRepository>();
        var service = CreateService(room, redis, cached);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "ja", "Bearer t");

        Assert.True(result.IsSuccess);
        Assert.Equal(SummaryVariantStatus.Ready, result.Value!.Status);
        Assert.Contains("日本語", result.Value.Content);
        Assert.False(result.Value.IsCanonical);
        redis.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A locale tag and its bare code are the same language. Rooms store `vi-VN` and pickers send
    /// `vi`; if these keyed differently the same rendering would be generated twice and served
    /// from whichever spelling the caller happened to use.
    /// </summary>
    [Fact]
    public async Task ALocaleTagFindsTheCacheItsBareCodeWrote()
    {
        var reader = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), reader, templateKey: "general", summaryLanguage: "");
        var cached = new TranslationRoomSummaryVariant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = room.Id,
            TemplateKey = "general",
            Language = "vi",
            Content = "{\"summary\":\"xin chào\",\"templateKey\":\"general\",\"summaryLanguage\":\"vi\"}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var redis = new Mock<IRedisStateRepository>();
        var service = CreateService(room, redis, cached);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "vi-VN", "Bearer t");

        Assert.Equal(SummaryVariantStatus.Ready, result.Value!.Status);
        Assert.Equal("vi", result.Value.Language);
        redis.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A rewrite is still a rewrite. `delivery` defaults to canonical so the host's button keeps
    /// replacing the room's summary, and an in-flight request published before this field existed
    /// keeps the meaning it was published with.
    /// </summary>
    [Fact]
    public async Task TheHostsRewriteStillTravelsAsCanonical()
    {
        var host = Guid.NewGuid();
        var room = RoomWithSummary(host, host, templateKey: "general", summaryLanguage: "");
        var redis = new Mock<IRedisStateRepository>();
        var service = CreateService(room, redis, variant: null);

        var result = await service.RegenerateSummaryAsync(room.Id, host, "standup", "ja", "Bearer t");

        Assert.True(result.IsSuccess);
        redis.Verify(
            r => r.StreamAddAsync(
                "assistant:summary_requests",
                It.Is<Dictionary<string, string>>(fields =>
                    fields["delivery"] == SummaryDelivery.Canonical)),
            Times.Once);
    }

    /// <summary>
    /// Absent means canonical, asserted on the constant itself rather than through the worker:
    /// getting this default backwards would turn every legacy request into a cache row nobody
    /// reads, and every reader's request into an overwrite of the host's summary.
    /// </summary>
    [Theory]
    [InlineData(null, SummaryDelivery.Canonical)]
    [InlineData("", SummaryDelivery.Canonical)]
    [InlineData("canonical", SummaryDelivery.Canonical)]
    [InlineData("nonsense", SummaryDelivery.Canonical)]
    [InlineData("variant", SummaryDelivery.Variant)]
    [InlineData("VARIANT", SummaryDelivery.Variant)]
    public void AnAbsentOrUnknownDeliveryMeansCanonical(string? given, string expected)
    {
        Assert.Equal(expected, SummaryDelivery.OrDefault(given));
    }

    /// <summary>
    /// A meeting with no published summary has no language to offer. Answering anything else
    /// spends an LLM call whose result the consumer would then have nowhere to put — the exact
    /// waste RegenerateSummaryAsync already learned to refuse.
    /// </summary>
    [Fact]
    public async Task AMeetingWithNoSummaryIsRefusedRatherThanQueued()
    {
        var reader = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), reader, templateKey: "general", summaryLanguage: "");
        room.TranslationRoomArtifacts.Clear();
        var redis = new Mock<IRedisStateRepository>();
        var service = CreateService(room, redis, variant: null);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "ja", "Bearer t");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        redis.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Somebody who may not read the room's artifacts may not read them in another language
    /// either. A rendering is the same content, so it cannot be a way around the gate.
    /// </summary>
    [Fact]
    public async Task SomebodyOutsideTheRoomCannotReadItInAnyLanguage()
    {
        var stranger = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), Guid.NewGuid(), templateKey: "general", summaryLanguage: "");
        var redis = new Mock<IRedisStateRepository>();
        var service = CreateService(room, redis, variant: null);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, stranger, "general", "ja", "Bearer t");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Unauthorized, result.ErrorCode);
        redis.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A meeting the host has NOT shared stays unshared in every language. Renderings ride the
    /// artifact policy rather than beside it, so this endpoint cannot become the way to read a
    /// summary the host kept to themselves.
    /// </summary>
    [Fact]
    public async Task AnUnsharedMeetingIsUnsharedInEveryLanguage()
    {
        var participant = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), participant, templateKey: "general", summaryLanguage: "");
        room.Settings = "{\"artifact_access\":\"HOST_ONLY\"}";
        var redis = new Mock<IRedisStateRepository>();
        var service = CreateService(room, redis, variant: null);

        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, participant, "general", "ja", "Bearer t");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Unauthorized, result.ErrorCode);
        redis.VerifyNoOtherCalls();
    }

    private static TranslationRoom RoomWithSummary(
        Guid hostId, Guid participantId, string templateKey, string summaryLanguage)
    {
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = hostId,
            Status = "ENDED",
            // ALL_PARTICIPANTS, because HOST_ONLY is the default and a participant cannot reach
            // the artifacts at all under it. That default is also why the overwrite this feature
            // fixes was reachable in exactly the situation it was reported in: the host had
            // shared the meeting's outputs with the room, which is what "host publish" means.
            Settings = "{\"artifact_access\":\"ALL_PARTICIPANTS\"}",
            TargetLanguages = "[\"en\",\"ja\"]"
        };

        room.TranslationRoomParticipants.Add(new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            UserId = participantId
        });

        room.TranslationRoomArtifacts.Add(new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            ArtifactType = "SUMMARY_EXPORT",
            Status = "COMPLETED",
            Content =
                "{\"summary\":\"published\",\"templateKey\":\"" + templateKey +
                "\",\"summaryLanguage\":\"" + summaryLanguage + "\"}",
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        });

        return room;
    }

    /// <summary>
    /// THE POLL THAT PAID FOR THE SAME PARAGRAPH TWENTY TIMES.
    ///
    /// Reading a meeting in another language is a GET that queues work, and the client polls that
    /// GET every four seconds for up to ninety while it waits. Nothing held the place, so every
    /// poll found no cached rendering and queued the whole job again. The log line has claimed
    /// "for the first reader who asked" since the day it was written; it simply was not true.
    /// </summary>
    [Fact]
    public async Task ASecondPollDoesNotQueueTheSameRenderingAgain()
    {
        var reader = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), reader, templateKey: "general", summaryLanguage: "");
        var redis = new Mock<IRedisStateRepository>();
        // AFTER CreateService, deliberately: it registers its own empty-Redis default for this
        // method, and in Moq the later Setup wins — configuring first would lose the callback.
        var service = CreateService(room, redis, variant: null);
        string? claimed = null;
        redis
            .Setup(item => item.StringSetIfAbsentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Callback((string _, string value, TimeSpan _) => claimed = value)
            .ReturnsAsync(true);
        // The claim the first call left behind, answered from the second call onwards.
        redis
            .Setup(item => item.StringGetAsync(It.Is<string>(key => key.StartsWith("summary_variant_inflight:"))))
            .ReturnsAsync(() => claimed);

        var first = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "ja", "Bearer t");
        var second = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "ja", "Bearer t");

        Assert.Equal(SummaryVariantStatus.Generating, first.Value!.Status);
        Assert.Equal(SummaryVariantStatus.Generating, second.Value!.Status);
        redis.Verify(
            item => item.StreamAddAsync(
                TranslationRoomConstants.SummaryRequestStream,
                It.IsAny<Dictionary<string, string>>()),
            Times.Once);
    }

    /// <summary>
    /// The job is queued under the SAME id the claim holds, or the claim could never be asked
    /// about — which is the only reason it stores an id rather than a flag.
    /// </summary>
    [Fact]
    public async Task TheQueuedJobCarriesTheIdTheClaimIsHeldUnder()
    {
        var reader = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), reader, templateKey: "general", summaryLanguage: "");
        var redis = new Mock<IRedisStateRepository>();
        // See the note above: CreateService registers a default for StringSetIfAbsentAsync, so
        // this has to come after it or the callback is overwritten.
        var service = CreateService(room, redis, variant: null);
        string? claimed = null;
        Dictionary<string, string>? queued = null;
        redis
            .Setup(item => item.StringSetIfAbsentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Callback((string _, string value, TimeSpan _) => claimed = value)
            .ReturnsAsync(true);
        redis
            .Setup(item => item.StreamAddAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .Callback((string _, Dictionary<string, string> fields) => queued = fields)
            .ReturnsAsync("1-0");
        await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "ja", "Bearer t");

        Assert.NotNull(claimed);
        Assert.Equal(claimed, queued!["request_id"]);
    }

    /// <summary>
    /// A rendering that failed says so, in the worker's own words.
    ///
    /// SummaryVariantStatus had no `failed` at all, and the absence WAS the bug: the endpoint
    /// could only keep answering "generating" until the client gave up after ninety seconds and
    /// said something had not arrived.
    /// </summary>
    [Fact]
    public async Task AFailedRenderingReportsTheReasonInsteadOfGeneratingForever()
    {
        var reader = Guid.NewGuid();
        var room = RoomWithSummary(Guid.NewGuid(), reader, templateKey: "general", summaryLanguage: "");
        var requestId = Guid.NewGuid().ToString();
        var redis = new Mock<IRedisStateRepository>();
        redis
            .Setup(item => item.StringGetAsync(It.Is<string>(key => key.StartsWith("summary_variant_inflight:"))))
            .ReturnsAsync(requestId);
        redis
            .Setup(item => item.StringGetAsync(
                TranslationRoomConstants.SummaryRewriteStatusKeyPrefix + requestId))
            .ReturnsAsync("{\"Status\":\"failed\",\"Error\":\"Could not read the transcript.\"}");

        var service = CreateService(room, redis, variant: null);
        var result = await service.GetOrQueueSummaryVariantAsync(room.Id, reader, "general", "ja", "Bearer t");

        Assert.Equal(SummaryVariantStatus.Failed, result.Value!.Status);
        Assert.Equal("Could not read the transcript.", result.Value.Error);
        // Released, so asking again starts a new run rather than replaying this answer forever.
        redis.Verify(
            item => item.KeyDeleteAsync(It.Is<string>(key => key.StartsWith("summary_variant_inflight:"))),
            Times.Once);
        redis.Verify(
            item => item.StreamAddAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
    }

    private static TranslationRoomArtifactService CreateService(
        TranslationRoom room,
        Mock<IRedisStateRepository> redis,
        TranslationRoomSummaryVariant? variant)
    {
        var roomRepository = new Mock<ITranslationRoomRepository>();
        roomRepository
            .Setup(repo => repo.FirstOrDefaultAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        // An empty Redis, which is what SET NX answers on a key nobody holds. Moq's default for a
        // bool is `false` — the answer Redis gives when somebody ELSE is already writing this
        // rendering — so leaving it unset would make every test here silently exercise the
        // "somebody beat us to it" branch and never queue anything. A test whose fake disagrees
        // with the real thing about a default is a test that passes for the wrong reason.
        // Individual tests override this to claim the key is taken.
        redis
            .Setup(item => item.StringSetIfAbsentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);

        // Keyed exactly as the database would key it, rather than answering any lookup with the
        // same row. That is what makes ALocaleTagFindsTheCacheItsBareCodeWrote a real test: the
        // cached row is 'vi', the caller sends 'vi-VN', and this only hits if the service
        // normalised on the way in.
        var variants = new Mock<ITranslationRoomSummaryVariantRepository>();
        variants
            .Setup(repo => repo.GetAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string template, string language, CancellationToken _) =>
                variant != null
                && variant.TemplateKey == template
                && variant.Language == language
                    ? variant
                    : null);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(roomRepository.Object);
        unitOfWork.SetupGet(work => work.TranslationRoomSummaryVariantRepository).Returns(variants.Object);

        return new TranslationRoomArtifactService(
            unitOfWork.Object,
            NullLogger<TranslationRoomArtifactService>.Instance,
            new Mock<IArtifactUrlSigner>().Object,
            redis.Object,
            new Mock<IArtifactsFinalizationQueue>().Object);
    }
}
