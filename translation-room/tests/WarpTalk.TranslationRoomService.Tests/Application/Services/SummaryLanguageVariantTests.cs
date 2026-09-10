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
