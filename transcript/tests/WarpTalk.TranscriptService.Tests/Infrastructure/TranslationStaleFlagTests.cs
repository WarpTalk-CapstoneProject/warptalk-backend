using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using Xunit;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

/// <summary>
/// WT-704. <see cref="SegmentTranslationLink.IsStale"/> — set when an STT correction changes the
/// line a translation was produced from, cleared when the retranslation arrives.
///
/// The case this file exists for is the retranslation that comes out word-for-word the same: it
/// dedups onto the content the segment is already linked to, so the consumer inserts nothing, and
/// unless that "already linked" branch clears the mark itself the line reads as outdated forever.
///
/// Exercised through the private ProcessTranslateMessageAsync by reflection, same seam as
/// TranscriptTimelineAnchorWriteTests. No database: the repositories are substitutes whose
/// predicates are compiled against in-memory rows, so they answer the question actually asked.
/// </summary>
public class TranslationStaleFlagTests
{
    // No room suffix and no meeting_id on the message: TryResolveRoomId fails, so the WT-587
    // retention gate is skipped and no TranslationRoom gRPC client is needed.
    private const string TranslateStream = "translate:results";

    private const string TargetLang = "en";

    [Fact]
    public async Task SameContentArrivingAgain_ClearsTheStaleMarkOnTheCurrentLink()
    {
        var fixture = new ConsumerFixture();
        var content = fixture.SeedContent("The corrected sentence.");
        var link = fixture.SeedLink(content, isCurrent: true, isStale: true);

        var handled = await fixture.ProcessTranslateAsync("The corrected sentence.");

        Assert.True(handled);
        Assert.False(link.IsStale);
        Assert.True(link.IsCurrent);
        Assert.Empty(fixture.AddedLinks);
        fixture.Links.Received(1).Update(link);
        await fixture.UnitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>A plain redelivery of an up-to-date translation must not queue a write.</summary>
    [Fact]
    public async Task SameContentArrivingAgain_OnANonStaleLink_WritesNothing()
    {
        var fixture = new ConsumerFixture();
        var content = fixture.SeedContent("Already fine.");
        fixture.SeedLink(content, isCurrent: true, isStale: false);

        Assert.True(await fixture.ProcessTranslateAsync("Already fine."));

        Assert.Empty(fixture.AddedLinks);
        fixture.Links.DidNotReceive().Update(Arg.Any<SegmentTranslationLink>());
    }

    /// <summary>
    /// A superseded link is history and is never shown; flipping its flag (or re-promoting it)
    /// here could let a redelivered old message override a newer translation.
    /// </summary>
    [Fact]
    public async Task SameContentArrivingAgain_OnASupersededLink_LeavesItAlone()
    {
        var fixture = new ConsumerFixture();
        var oldContent = fixture.SeedContent("Old wording.");
        var oldLink = fixture.SeedLink(oldContent, isCurrent: false, isStale: true);

        Assert.True(await fixture.ProcessTranslateAsync("Old wording."));

        Assert.True(oldLink.IsStale);
        Assert.False(oldLink.IsCurrent);
        fixture.Links.DidNotReceive().Update(Arg.Any<SegmentTranslationLink>());
    }

    [Fact]
    public async Task NewContent_IsLinkedNotStale_AndSupersedesTheStaleHead()
    {
        var fixture = new ConsumerFixture();
        var oldContent = fixture.SeedContent("The original sentence.");
        var oldLink = fixture.SeedLink(oldContent, isCurrent: true, isStale: true);

        var handled = await fixture.ProcessTranslateAsync("A different, corrected sentence.");

        Assert.True(handled);
        var added = Assert.Single(fixture.AddedLinks);
        Assert.True(added.IsCurrent);
        Assert.False(added.IsStale);
        Assert.Equal(TargetLang, added.TargetLanguage);
        Assert.NotEqual(oldContent.Id, added.TranslationContentId);

        Assert.False(oldLink.IsCurrent);
        fixture.Links.Received(1).Update(oldLink);
    }

    [Fact]
    public async Task FirstTranslationOfALine_IsLinkedNotStale()
    {
        var fixture = new ConsumerFixture();

        Assert.True(await fixture.ProcessTranslateAsync("Brand new line."));

        var added = Assert.Single(fixture.AddedLinks);
        Assert.False(added.IsStale);
        Assert.True(added.IsCurrent);
    }

    [Fact]
    public async Task GetTranslationsAsync_CarriesTheLinkStaleFlag()
    {
        var transcript = new Transcript
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            Status = "COMPLETED",
            SourceLanguage = "vi",
            CreatedAt = DateTime.UtcNow,
        };
        var freshSegment = new TranscriptSegment { Id = Guid.NewGuid(), TranscriptId = transcript.Id, SequenceOrder = 1 };
        var staleSegment = new TranscriptSegment { Id = Guid.NewGuid(), TranscriptId = transcript.Id, SequenceOrder = 2 };
        var freshContent = Content(transcript.WorkspaceId, "Fresh.");
        var staleContent = Content(transcript.WorkspaceId, "Outdated.");
        var links = new List<SegmentTranslationLink>
        {
            new() { SegmentId = freshSegment.Id, TranslationContentId = freshContent.Id, TargetLanguage = TargetLang, IsCurrent = true, IsStale = false },
            new() { SegmentId = staleSegment.Id, TranslationContentId = staleContent.Id, TargetLanguage = TargetLang, IsCurrent = true, IsStale = true },
        };

        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transcripts = Substitute.For<ITranscriptRepository>();
        transcripts.GetByIdAsync(transcript.Id, Arg.Any<CancellationToken>()).Returns(transcript);
        // Built before being handed to Returns(): configuring one substitute inside another's
        // Returns() call confuses NSubstitute's last-call tracking.
        var segments = Repository<ITranscriptSegmentRepository, TranscriptSegment>(
            new List<TranscriptSegment> { freshSegment, staleSegment });
        var linkRepository = Repository<ISegmentTranslationLinkRepository, SegmentTranslationLink>(links);
        var contents = Repository<ITranslationContentRepository, TranslationContent>(
            new List<TranslationContent> { freshContent, staleContent });
        unitOfWork.Transcripts.Returns(transcripts);
        unitOfWork.TranscriptSegments.Returns(segments);
        unitOfWork.SegmentTranslationLinks.Returns(linkRepository);
        unitOfWork.TranslationContents.Returns(contents);

        var readAccess = Substitute.For<ITranscriptReadAccess>();
        readAccess.CanReadRoomTranscriptAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        var service = new TranscriptQueryService(unitOfWork, readAccess, NullLogger<TranscriptQueryService>.Instance);

        var result = await service.GetTranslationsAsync(transcript.Id, Guid.NewGuid());

        Assert.True(result.IsSuccess);
        var items = result.Value!.Items.ToList();
        Assert.Equal(2, items.Count);
        Assert.False(items.Single(i => i.SegmentId == freshSegment.Id).IsStale);
        Assert.True(items.Single(i => i.SegmentId == staleSegment.Id).IsStale);
    }

    private static TranslationContent Content(Guid workspaceId, string text) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        TextHash = TranslationTextHash.Of(text),
        TargetLanguage = TargetLang,
        TranslatedText = text,
        TranslatorModel = "test",
        Status = "done",
    };

    /// <summary>A substitute repository whose FindAsync compiles the consumer's predicate against
    /// <paramref name="rows"/> — a stub that ignored it would let a wrong filter pass.</summary>
    private static TRepo Repository<TRepo, TEntity>(List<TEntity> rows)
        where TRepo : class, IGenericRepository<TEntity>
        where TEntity : class
    {
        var repository = Substitute.For<TRepo>();
        repository
            .FindAsync(Arg.Any<Expression<Func<TEntity, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                rows.Where(call.Arg<Expression<Func<TEntity, bool>>>().Compile()).ToList().AsEnumerable()));
        return repository;
    }

    /// <summary>One transcript with one stored segment, wired so ProcessTranslateMessageAsync runs
    /// its real find-or-create-content and link logic.</summary>
    private sealed class ConsumerFixture
    {
        private readonly TranscriptRedisConsumerService _service;
        private readonly List<TranslationContent> _contents = new();
        private readonly List<SegmentTranslationLink> _links = new();

        public ConsumerFixture()
        {
            Transcript = new Transcript
            {
                Id = Guid.NewGuid(),
                TranslationRoomId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                Status = "COMPLETED",
                SourceLanguage = "vi",
                IsActive = true,
                IsCurrent = true,
                CreatedAt = DateTime.UtcNow,
            };
            Segment = new TranscriptSegment { Id = Guid.NewGuid(), TranscriptId = Transcript.Id, SequenceOrder = 1 };

            var transcripts = Substitute.For<ITranscriptRepository>();
            transcripts.GetByIdAsync(Transcript.Id, Arg.Any<CancellationToken>()).Returns(Transcript);

            var segments = Substitute.For<ITranscriptSegmentRepository>();
            segments.GetByIdAsync(Segment.Id, Arg.Any<CancellationToken>()).Returns(Segment);

            var contents = Repository<ITranslationContentRepository, TranslationContent>(_contents);
            contents
                .When(r => r.AddAsync(Arg.Any<TranslationContent>(), Arg.Any<CancellationToken>()))
                .Do(call => _contents.Add(call.Arg<TranslationContent>()));

            Links = Repository<ISegmentTranslationLinkRepository, SegmentTranslationLink>(_links);
            Links
                .When(r => r.AddAsync(Arg.Any<SegmentTranslationLink>(), Arg.Any<CancellationToken>()))
                .Do(call => AddedLinks.Add(call.Arg<SegmentTranslationLink>()));

            UnitOfWork = Substitute.For<IUnitOfWork>();
            UnitOfWork.Transcripts.Returns(transcripts);
            UnitOfWork.TranscriptSegments.Returns(segments);
            UnitOfWork.TranslationContents.Returns(contents);
            UnitOfWork.SegmentTranslationLinks.Returns(Links);

            var services = new ServiceCollection();
            services.AddScoped(_ => UnitOfWork);

            var redis = Substitute.For<IConnectionMultiplexer>();
            redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Substitute.For<IDatabase>());

            _service = new TranscriptRedisConsumerService(
                redis,
                NullLogger<TranscriptRedisConsumerService>.Instance,
                services.BuildServiceProvider());
        }

        public Transcript Transcript { get; }

        public TranscriptSegment Segment { get; }

        public IUnitOfWork UnitOfWork { get; }

        public ISegmentTranslationLinkRepository Links { get; }

        public List<SegmentTranslationLink> AddedLinks { get; } = new();

        public TranslationContent SeedContent(string text)
        {
            var content = Content(Transcript.WorkspaceId, text);
            _contents.Add(content);
            return content;
        }

        public SegmentTranslationLink SeedLink(TranslationContent content, bool isCurrent, bool isStale)
        {
            var link = new SegmentTranslationLink
            {
                SegmentId = Segment.Id,
                TranslationContentId = content.Id,
                TargetLanguage = content.TargetLanguage,
                IsCurrent = isCurrent,
                IsStale = isStale,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            };
            _links.Add(link);
            return link;
        }

        public Task<bool> ProcessTranslateAsync(string translatedText)
        {
            var entry = new StreamEntry("1-0", new[]
            {
                // The composite per-sentence id translation_worker actually emits.
                new NameValueEntry("segment_id", $"{Segment.Id}-c0"),
                new NameValueEntry("translated_text", translatedText),
                new NameValueEntry("target_lang", TargetLang),
                new NameValueEntry("translator_model", "test"),
            });

            return (Task<bool>)ProcessTranslate.Invoke(
                _service, new object[] { TranslateStream, entry, CancellationToken.None })!;
        }

        private static readonly MethodInfo ProcessTranslate =
            typeof(TranscriptRedisConsumerService).GetMethod(
                "ProcessTranslateMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(
                nameof(TranscriptRedisConsumerService), "ProcessTranslateMessageAsync");
    }
}
