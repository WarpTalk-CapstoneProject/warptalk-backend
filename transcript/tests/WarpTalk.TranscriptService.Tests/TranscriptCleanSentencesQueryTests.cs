using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// WT-716. GET /api/v1/transcripts/{id}/clean-sentences — the transcript's text in a different
/// shape, so it must be gated exactly like /segments and ordered the way the meeting was spoken.
/// </summary>
public class TranscriptCleanSentencesQueryTests
{
    private static readonly Guid TranscriptId = Guid.NewGuid();
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task AReaderTheTranscriptGateRefuses_GetsNoSentences()
    {
        // The same predicate as /segments, ENDED-room artifact rule included. A looser second path to
        // the same words is how a withheld record stayed readable before (TranscriptReadAccess).
        var context = Build(canRead: false, sentences: [Sentence(Guid.NewGuid(), [Guid.NewGuid()])]);

        var result = await context.Service.GetCleanSentencesAsync(TranscriptId, UserId);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
        await context.Sentences.DidNotReceive().GetByTranscriptIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADeletedTranscript_IsNotFound()
    {
        var context = Build(canRead: true, sentences: [], deleted: true);

        var result = await context.Service.GetCleanSentencesAsync(TranscriptId, UserId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task Sentences_AreOrderedByTheirSegments_NotByWhenTheyWereWritten()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var s3 = Guid.NewGuid();
        var late = Sentence(Guid.NewGuid(), [s1, s2], createdAt: DateTime.UtcNow.AddMinutes(5)); // rewritten late
        var middle = Sentence(Guid.NewGuid(), [s3], createdAt: DateTime.UtcNow);
        var unanchored = Sentence(Guid.NewGuid(), [Guid.NewGuid()], createdAt: DateTime.UtcNow.AddMinutes(-5));

        var context = Build(
            canRead: true,
            sentences: [unanchored, middle, late],
            segments: [Segment(s1, 1), Segment(s2, 2), Segment(s3, 3)]);

        var result = await context.Service.GetCleanSentencesAsync(TranscriptId, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.TotalCount);
        Assert.Equal([late.Id, middle.Id, unanchored.Id], result.Value.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Paging_AppliesAfterOrdering()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var first = Sentence(Guid.NewGuid(), [a]);
        var second = Sentence(Guid.NewGuid(), [b]);
        var context = Build(canRead: true, sentences: [second, first], segments: [Segment(a, 1), Segment(b, 2)]);

        var result = await context.Service.GetCleanSentencesAsync(TranscriptId, UserId, skip: 1, take: 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.TotalCount);
        Assert.Equal(second.Id, Assert.Single(result.Value.Items).Id);
    }

    [Fact]
    public void TheDto_CarriesEveryFieldTheWebReads()
    {
        var segmentIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var speaker = Guid.NewGuid();
        var entity = new TranscriptCleanSentence
        {
            Id = Guid.NewGuid(),
            TranscriptId = TranscriptId,
            SpeakerParticipantId = speaker,
            SegmentIds = segmentIds,
            CleanText = "We ship on Monday.",
            Language = "en",
            Flags = ["self_repair"],
            Source = "llm",
            Revision = 4,
            UpdatedAt = new DateTime(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc),
        };

        var dto = entity.ToDto();

        Assert.Equal(entity.Id, dto.Id);
        Assert.Equal(speaker, dto.SpeakerId);
        Assert.Equal(segmentIds, dto.SegmentIds);
        Assert.Equal("We ship on Monday.", dto.CleanText);
        Assert.Equal("en", dto.Language);
        Assert.Equal(["self_repair"], dto.Flags);
        Assert.Equal("llm", dto.Source);
        Assert.Equal(4, dto.Revision);
        Assert.Equal(entity.UpdatedAt, dto.UpdatedAt);
    }

    [Fact]
    public void TheSegmentDto_SendsNullCleanTextAsNull_AndFlagsAsAnEmptyList()
    {
        var dto = Segment(Guid.NewGuid(), 1).ToDto();

        Assert.Null(dto.CleanText);
        Assert.NotNull(dto.CleanFlags);
        Assert.Empty(dto.CleanFlags!);
    }

    [Fact]
    public void TheSegmentDto_CarriesCleanTextAndFlags()
    {
        var segment = Segment(Guid.NewGuid(), 1);
        segment.CleanText = "";
        segment.CleanFlags = ["filler_only"];

        var dto = segment.ToDto();

        Assert.Equal(string.Empty, dto.CleanText);
        Assert.Equal(["filler_only"], dto.CleanFlags);
    }

    private static TranscriptCleanSentence Sentence(Guid id, Guid[] segmentIds, DateTime? createdAt = null) => new()
    {
        Id = id,
        TranscriptId = TranscriptId,
        SegmentIds = segmentIds,
        CleanText = "text",
        Language = "en",
        Source = "llm",
        Revision = 1,
        CreatedAt = createdAt ?? DateTime.UtcNow,
        UpdatedAt = createdAt ?? DateTime.UtcNow,
    };

    private static TranscriptSegment Segment(Guid id, int sequenceOrder) => new()
    {
        Id = id,
        TranscriptId = TranscriptId,
        SpeakerName = "A",
        OriginalText = "raw",
        OriginalLanguage = "en",
        SequenceOrder = sequenceOrder,
    };

    private sealed record Context(TranscriptQueryService Service, ITranscriptCleanSentenceRepository Sentences);

    private static Context Build(
        bool canRead,
        IReadOnlyList<TranscriptCleanSentence> sentences,
        IReadOnlyList<TranscriptSegment>? segments = null,
        bool deleted = false)
    {
        var transcripts = Substitute.For<ITranscriptRepository>();
        transcripts.GetByIdAsync(TranscriptId, Arg.Any<CancellationToken>()).Returns(new Transcript
        {
            Id = TranscriptId,
            TranslationRoomId = RoomId,
            WorkspaceId = Guid.NewGuid(),
            Status = "COMPLETED",
            SourceLanguage = "en",
            DeletedAt = deleted ? DateTime.UtcNow : null,
        });

        var segmentRows = (segments ?? []).ToList();
        var segmentRepository = Substitute.For<ITranscriptSegmentRepository>();
        segmentRepository
            .FindAsync(Arg.Any<Expression<Func<TranscriptSegment, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                segmentRows.Where(call.Arg<Expression<Func<TranscriptSegment, bool>>>().Compile()).ToList().AsEnumerable()));

        var sentenceRepository = Substitute.For<ITranscriptCleanSentenceRepository>();
        sentenceRepository.GetByTranscriptIdAsync(TranscriptId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TranscriptCleanSentence>>(sentences.ToList()));

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.Transcripts.Returns(transcripts);
        unitOfWork.TranscriptSegments.Returns(segmentRepository);
        unitOfWork.TranscriptCleanSentences.Returns(sentenceRepository);

        var readAccess = Substitute.For<ITranscriptReadAccess>();
        readAccess.CanReadRoomTranscriptAsync(RoomId, UserId, Arg.Any<CancellationToken>()).Returns(canRead);

        return new Context(
            new TranscriptQueryService(unitOfWork, readAccess, NullLogger<TranscriptQueryService>.Instance),
            sentenceRepository);
    }
}
