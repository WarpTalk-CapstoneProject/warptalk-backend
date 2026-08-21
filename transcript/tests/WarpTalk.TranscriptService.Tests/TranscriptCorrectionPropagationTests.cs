using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// What happens to a line's TRANSLATIONS when the line itself is corrected.
///
/// Nothing did, for as long as the feature existed. Submitting a correction published a message to
/// <c>translate:requests:{roomId}</c> — a stream no worker has ever consumed, carrying no target
/// language — under a comment asserting translate_worker picked it up. So the transcript showed the
/// fix and every translation of it kept the mistake, indefinitely.
/// </summary>
public class TranscriptCorrectionPropagationTests
{
    private static readonly Guid TranscriptId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SegmentId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid RoomId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid WorkspaceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task AnSttCorrection_RedoesEveryTranslationOfTheLineItChanged()
    {
        // Correcting what somebody said leaves each translation of that line describing a sentence
        // nobody spoke — in every language at once, not just the one the reader is looking at.
        var context = Build([Link("en"), Link("ja")]);

        var result = await context.Service.SubmitCorrectionAsync(
            TranscriptId, SegmentId, UserId, Correction("STT", "what was actually said"));

        Assert.True(result.IsSuccess);
        await context.Backfill.Received(1).RequestRetranslationAsync(SegmentId, Arg.Any<CancellationToken>());
        Assert.Equal("what was actually said", context.Segment.OriginalText);
        Assert.True(context.Segment.IsCorrected);
    }

    [Fact]
    public async Task AnSttCorrection_OnALineNothingEverTranslatedQueuesNothing()
    {
        var context = Build([]);

        var result = await context.Service.SubmitCorrectionAsync(
            TranscriptId, SegmentId, UserId, Correction("STT", "what was actually said"));

        Assert.True(result.IsSuccess);
        await context.Backfill.DidNotReceive().RequestRetranslationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        // And it says so. This column was hardcoded true on every correction ever made, while
        // nothing retranslated anything.
        Assert.False(context.SavedCorrection!.TriggeredRetranslation);
    }

    [Fact]
    public async Task AnSttCorrection_RecordsThatItTriggeredOne()
    {
        var context = Build([Link("en")]);

        await context.Service.SubmitCorrectionAsync(
            TranscriptId, SegmentId, UserId, Correction("STT", "what was actually said"));

        Assert.True(context.SavedCorrection!.TriggeredRetranslation);
    }

    [Fact]
    public async Task AnMtCorrection_BecomesTheTranslationItselfRatherThanAskingTheMachineAgain()
    {
        // A person read the machine's output and typed what it should have said. Handing that text
        // back to the machine would discard the judgement being recorded.
        var context = Build([Link("en")]);

        var result = await context.Service.SubmitCorrectionAsync(
            TranscriptId, SegmentId, UserId, Correction("MT", "the wording a person chose", "en-US"));

        Assert.True(result.IsSuccess);
        await context.Backfill.DidNotReceive().RequestRetranslationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        var content = Assert.Single(context.AddedContents);
        Assert.Equal("the wording a person chose", content.TranslatedText);
        Assert.Equal("en", content.TargetLanguage);
        Assert.Equal("human", content.TranslatorModel);

        // The two columns translation_contents has modelled since it was designed and nothing had
        // ever written.
        Assert.True(content.IsRetranslated);
        Assert.Equal(context.Links[0].TranslationContentId, content.PreviousTranslationContentId);
    }

    [Fact]
    public async Task AnMtCorrection_MakesItsOwnTextTheCurrentOneAndDemotesWhatItReplaced()
    {
        var context = Build([Link("en")]);

        await context.Service.SubmitCorrectionAsync(
            TranscriptId, SegmentId, UserId, Correction("MT", "the wording a person chose", "en"));

        // segment_translation_links_current_unique_idx allows one current row per (segment,
        // language): the old one must be off before the new one is on.
        Assert.False(context.Links[0].IsCurrent);

        var added = Assert.Single(context.AddedLinks);
        Assert.True(added.IsCurrent);
        Assert.Equal("en", added.TargetLanguage);
        // Nothing was delivered — this text was typed after the meeting and no participant heard it.
        Assert.Null(added.DeliveredAt);
    }

    [Fact]
    public async Task AnMtCorrection_FindsTheLinkEvenWhenTheClientSendsALocaleTag()
    {
        // A room hands out "en-US" and the link stores "en". Comparing the raw strings found no
        // link, so the correction named no row as its subject — and would now replace nothing.
        var context = Build([Link("en")]);

        await context.Service.SubmitCorrectionAsync(
            TranscriptId, SegmentId, UserId, Correction("MT", "the wording a person chose", "en-US"));

        Assert.Equal(context.Links[0].TranslationContentId, context.SavedCorrection!.TranslationContentId);
    }

    [Fact]
    public async Task AnMtCorrection_WithoutATargetLanguageIsRefused()
    {
        var context = Build([Link("en")]);

        var result = await context.Service.SubmitCorrectionAsync(
            TranscriptId, SegmentId, UserId, Correction("MT", "the wording a person chose"));

        Assert.False(result.IsSuccess);
        Assert.Equal("BAD_REQUEST", result.ErrorCode);
    }

    private static CreateCorrectionDto Correction(string type, string corrected, string? language = null) =>
        new(UserId, "what the machine heard", corrected, type, language);

    private static SegmentTranslationLink Link(string language) => new()
    {
        SegmentId = SegmentId,
        TranslationContentId = Guid.NewGuid(),
        TargetLanguage = language,
        IsCurrent = true,
    };

    private sealed class Context
    {
        public TranscriptCorrectionService Service { get; set; } = null!;
        public ITranscriptTranslationBackfillService Backfill { get; init; } = null!;
        public TranscriptSegment Segment { get; init; } = null!;
        public List<SegmentTranslationLink> Links { get; init; } = [];
        public List<TranslationContent> AddedContents { get; init; } = [];
        public List<SegmentTranslationLink> AddedLinks { get; init; } = [];
        public TranscriptCorrection? SavedCorrection { get; set; }
    }

    private static Context Build(IReadOnlyList<SegmentTranslationLink> links)
    {
        var segment = new TranscriptSegment
        {
            Id = SegmentId,
            TranscriptId = TranscriptId,
            OriginalText = "what the machine heard",
            OriginalLanguage = "vi",
        };

        var context = new Context
        {
            Backfill = Substitute.For<ITranscriptTranslationBackfillService>(),
            Segment = segment,
            Links = links.ToList(),
        };

        var unitOfWork = Substitute.For<IUnitOfWork>();

        var segments = Substitute.For<ITranscriptSegmentRepository>();
        segments.GetByIdAsync(SegmentId, Arg.Any<CancellationToken>()).Returns(segment);
        unitOfWork.TranscriptSegments.Returns(segments);

        var transcripts = Substitute.For<ITranscriptRepository>();
        transcripts.GetByIdAsync(TranscriptId, Arg.Any<CancellationToken>()).Returns(new Transcript
        {
            Id = TranscriptId,
            TranslationRoomId = RoomId,
            WorkspaceId = WorkspaceId,
            Status = "FINALIZED",
            SourceLanguage = "vi",
        });
        unitOfWork.Transcripts.Returns(transcripts);

        // The predicates are compiled and applied, so the fixture answers the same question the
        // database would rather than whatever the test happened to hand back.
        var linkRepository = Substitute.For<ISegmentTranslationLinkRepository>();
        linkRepository
            .FindAsync(Arg.Any<Expression<Func<SegmentTranslationLink, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                context.Links.Where(call.Arg<Expression<Func<SegmentTranslationLink, bool>>>().Compile())));
        linkRepository
            .When(r => r.AddAsync(Arg.Any<SegmentTranslationLink>(), Arg.Any<CancellationToken>()))
            .Do(call => context.AddedLinks.Add(call.Arg<SegmentTranslationLink>()));
        unitOfWork.SegmentTranslationLinks.Returns(linkRepository);

        var contentRepository = Substitute.For<ITranslationContentRepository>();
        contentRepository
            .FindAsync(Arg.Any<Expression<Func<TranslationContent, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                context.AddedContents.Where(call.Arg<Expression<Func<TranslationContent, bool>>>().Compile())));
        contentRepository
            .When(r => r.AddAsync(Arg.Any<TranslationContent>(), Arg.Any<CancellationToken>()))
            .Do(call => context.AddedContents.Add(call.Arg<TranslationContent>()));
        unitOfWork.TranslationContents.Returns(contentRepository);

        var corrections = Substitute.For<ITranscriptCorrectionRepository>();
        corrections
            .When(r => r.AddAsync(Arg.Any<TranscriptCorrection>(), Arg.Any<CancellationToken>()))
            .Do(call => context.SavedCorrection = call.Arg<TranscriptCorrection>());
        unitOfWork.TranscriptCorrections.Returns(corrections);

        var readAccess = Substitute.For<ITranscriptReadAccess>();
        readAccess.CanReadRoomTranscriptAsync(RoomId, UserId, Arg.Any<CancellationToken>()).Returns(true);

        // The room gRPC client is only reached by FinalizeTranscriptAsync, which none of these
        // exercise; a null here fails loudly if that ever stops being true.
        context.Service = new TranscriptCorrectionService(
            unitOfWork,
            readAccess,
            null!,
            context.Backfill,
            NullLogger<TranscriptCorrectionService>.Instance);

        return context;
    }
}
