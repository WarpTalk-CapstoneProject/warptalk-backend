using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// WT-472 — importing a spreadsheet of glossary terms in one request.
///
/// The behaviours worth pinning are all about NOT LYING ABOUT WHAT LANDED: the counter is adjusted
/// once by however many rows were written, duplicates are skipped but counted, and the dedupe key
/// cannot collide two unrelated pairs.
/// </summary>
public class GlossaryBulkImportTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IGlossaryRepository _glossaries;
    private readonly IGlossaryTermRepository _terms;
    private readonly GlossaryService _service;

    public GlossaryBulkImportTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _glossaries = Substitute.For<IGlossaryRepository>();
        _terms = Substitute.For<IGlossaryTermRepository>();
        _unitOfWork.Glossaries.Returns(_glossaries);
        _unitOfWork.GlossaryTerms.Returns(_terms);

        // The embedding publish swallows every exception internally, so a stubbed IDatabase keeps
        // the import path from depending on Redis at all.
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase().ReturnsForAnyArgs(Substitute.For<IDatabase>());

        _service = new GlossaryService(
            _unitOfWork,
            Substitute.For<ILogger<GlossaryService>>(),
            redis);
    }

    private Glossary StubGlossary(int termCount = 0)
    {
        var glossary = new Glossary
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            Name = "Aviation",
            TermCount = termCount,
        };
        _glossaries.GetByIdAsync(glossary.Id, Arg.Any<CancellationToken>()).Returns(glossary);
        return glossary;
    }

    private void StubExistingTerms(params GlossaryTerm[] existing)
    {
        _terms
            .FindAsync(Arg.Any<Expression<Func<GlossaryTerm, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(existing);
    }

    private static CreateGlossaryTermDto Term(string source, string target) =>
        new(source, target, null, null, null, null, null, Priority: 5);

    [Fact]
    public async Task BulkImportTermsAsync_WritesEveryRow_AndBumpsTheCounterOnce()
    {
        var glossary = StubGlossary();
        StubExistingTerms();

        var result = await _service.BulkImportTermsAsync(
            glossary.Id,
            new BulkImportGlossaryTermsDto(new[]
            {
                Term("go-around", "bay lại"),
                Term("holding pattern", "vòng chờ"),
                Term("clearance", "phép"),
            }));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.Imported);
        Assert.Equal(0, result.Value.Skipped);
        Assert.Equal(3, glossary.TermCount);

        // ONE SaveChanges for the whole file. Looping AddTermAsync would have been three, and a
        // client dying between them leaves TermCount describing a glossary that does not exist.
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkImportTermsAsync_SkipsTermsAlreadyStored_AndSaysHowMany()
    {
        var glossary = StubGlossary(termCount: 1);
        StubExistingTerms(new GlossaryTerm
        {
            Id = Guid.NewGuid(),
            GlossaryId = glossary.Id,
            SourceTerm = "go-around",
            TargetTerm = "bay lại",
        });

        var result = await _service.BulkImportTermsAsync(
            glossary.Id,
            new BulkImportGlossaryTermsDto(new[]
            {
                // Same pair, different casing — an import is usually a second pass over a
                // spreadsheet somebody kept editing.
                Term("GO-AROUND", "Bay lại"),
                Term("clearance", "phép"),
            }));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.Imported);
        // Reported, not silently dropped: "2 imported" when one was written is how somebody comes
        // to believe a term exists that does not.
        Assert.Equal(1, result.Value.Skipped);
        Assert.Single(result.Value.Errors);
        Assert.Equal(2, glossary.TermCount);
    }

    [Fact]
    public async Task BulkImportTermsAsync_DeduplicatesWithinTheIncomingFile()
    {
        var glossary = StubGlossary();
        StubExistingTerms();

        var result = await _service.BulkImportTermsAsync(
            glossary.Id,
            new BulkImportGlossaryTermsDto(new[]
            {
                Term("clearance", "phép"),
                Term("clearance", "phép"),
            }));

        Assert.True(result.IsSuccess);
        // Neither row existed when the request arrived, so checking only the STORED rows would have
        // written both.
        Assert.Equal(1, result.Value!.Imported);
        Assert.Equal(1, result.Value.Skipped);
    }

    /// <summary>
    /// Two different terms stay two terms. ("voice","clone") and ("voicec","lone") were the shape
    /// that collided when the key concatenated the pair without a separator; the key is the source
    /// term alone now, and these two sources differ, so both still land.
    /// </summary>
    [Fact]
    public async Task BulkImportTermsAsync_DoesNotTreatAShiftedPairAsADuplicate()
    {
        var glossary = StubGlossary();
        StubExistingTerms();

        var result = await _service.BulkImportTermsAsync(
            glossary.Id,
            new BulkImportGlossaryTermsDto(new[]
            {
                Term("voice", "clone"),
                Term("voicec", "lone"),
            }));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Imported);
        Assert.Equal(0, result.Value.Skipped);
    }

    [Fact]
    public async Task BulkImportTermsAsync_RejectsRowsMissingASide_WithoutFailingTheFile()
    {
        var glossary = StubGlossary();
        StubExistingTerms();

        var result = await _service.BulkImportTermsAsync(
            glossary.Id,
            new BulkImportGlossaryTermsDto(new[]
            {
                Term("clearance", "phép"),
                Term("  ", "orphan target"),
            }));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.Imported);
        Assert.Equal(1, result.Value.Skipped);
        Assert.Single(result.Value.Errors);
    }

    [Fact]
    public async Task BulkImportTermsAsync_ReturnsNotFound_ForAnUnknownGlossary()
    {
        _glossaries
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Glossary?)null);

        var result = await _service.BulkImportTermsAsync(
            Guid.NewGuid(),
            new BulkImportGlossaryTermsDto(new[] { Term("clearance", "phép") }));

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Nothing written means nothing committed. Saving anyway would stamp `UpdatedAt` on a glossary
    /// whose contents did not change, which makes an "updated" timestamp meaningless.
    /// </summary>
    [Fact]
    public async Task BulkImportTermsAsync_DoesNotCommit_WhenEveryRowWasSkipped()
    {
        var glossary = StubGlossary(termCount: 1);
        StubExistingTerms(new GlossaryTerm
        {
            Id = Guid.NewGuid(),
            GlossaryId = glossary.Id,
            SourceTerm = "clearance",
            TargetTerm = "phép",
        });

        var result = await _service.BulkImportTermsAsync(
            glossary.Id,
            new BulkImportGlossaryTermsDto(new[] { Term("clearance", "phép") }));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.Imported);
        Assert.Equal(1, glossary.TermCount);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// WT-601 — the file names one term twice with two different translations.
    ///
    /// This is what a 30-row spreadsheet import came back as HTTP 500 for. The dedupe key was
    /// (source, target), so both rows passed it; the database's unique index is
    /// (glossary_id, source_term), so the second INSERT was refused, the DbUpdateException fell
    /// into the catch-all, and the reader was told "Something went wrong on the server".
    ///
    /// A glossary answers "how is this term translated", so a second answer for the same term is a
    /// duplicate whatever it says — skipped, counted, and named in the errors.
    /// </summary>
    [Fact]
    public async Task BulkImportTermsAsync_SkipsASecondTranslationOfTheSameTerm()
    {
        var glossary = StubGlossary();
        StubExistingTerms();

        var result = await _service.BulkImportTermsAsync(
            glossary.Id,
            new BulkImportGlossaryTermsDto(new[]
            {
                Term("clearance", "phép"),
                Term("clearance", "giấy phép"),
            }));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.Imported);
        Assert.Equal(1, result.Value.Skipped);
        Assert.Single(result.Value.Errors);
        Assert.Contains("clearance", result.Value.Errors[0]);
        Assert.Equal(1, glossary.TermCount);
    }

    /// <summary>
    /// The stored side of the same rule: re-importing a term that is already here with a different
    /// translation is a skip, and the message names the translation already in place so the reader
    /// knows what to edit.
    /// </summary>
    [Fact]
    public async Task BulkImportTermsAsync_SkipsATermStoredWithADifferentTranslation()
    {
        var glossary = StubGlossary(termCount: 1);
        StubExistingTerms(new GlossaryTerm
        {
            Id = Guid.NewGuid(),
            GlossaryId = glossary.Id,
            SourceTerm = "clearance",
            TargetTerm = "phép",
        });

        var result = await _service.BulkImportTermsAsync(
            glossary.Id,
            new BulkImportGlossaryTermsDto(new[] { Term("clearance", "giấy phép") }));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.Imported);
        Assert.Equal(1, result.Value.Skipped);
        Assert.Contains("phép", result.Value.Errors[0]);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
