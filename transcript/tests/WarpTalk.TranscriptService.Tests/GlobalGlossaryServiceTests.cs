using System;
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

public class GlobalGlossaryServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IGlobalGlossaryTermRepository _termsRepo;
    private readonly IGlobalGlossaryAuditRepository _auditsRepo;
    private readonly IDatabase _redisDatabase;
    private readonly GlobalGlossaryService _service;

    public GlobalGlossaryServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _termsRepo = Substitute.For<IGlobalGlossaryTermRepository>();
        _auditsRepo = Substitute.For<IGlobalGlossaryAuditRepository>();
        _unitOfWork.GlobalGlossaryTerms.Returns(_termsRepo);
        _unitOfWork.GlobalGlossaryAudits.Returns(_auditsRepo);

        // TryPublishEmbeddingIndexRequestAsync/TryPublishEmbeddingDeleteRequestAsync swallow
        // every exception internally (logged only, never propagated — see
        // GlobalGlossaryService.cs), so a stubbed IDatabase lets the assertions below verify
        // exactly what gets published instead of just tolerating a silent no-op.
        var redis = Substitute.For<IConnectionMultiplexer>();
        _redisDatabase = Substitute.For<IDatabase>();
        redis.GetDatabase().ReturnsForAnyArgs(_redisDatabase);

        _service = new GlobalGlossaryService(
            _unitOfWork,
            Substitute.For<ILogger<GlobalGlossaryService>>(),
            redis);
    }

    private static string? FieldValue(NameValueEntry[] entries, string name) =>
        entries.FirstOrDefault(e => e.Name == name).Value.ToString();

    private static CreateGlobalGlossaryTermDto NewCreateDto(string term = "architect") =>
        new(term, term, null, null, null, "A design role.", null, Priority: 5);

    [Fact]
    public async Task CreateTermAsync_ReturnsFailure_WhenDuplicateKeyExists()
    {
        _termsRepo.ExistsAsync(Arg.Any<Expression<Func<GlobalGlossaryTerm, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.CreateTermAsync(NewCreateDto(), Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("BAD_REQUEST", result.ErrorCode);
        await _termsRepo.DidNotReceive().AddAsync(Arg.Any<GlobalGlossaryTerm>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTermAsync_CreatesDraftTerm_WhenNoDuplicateExists()
    {
        _termsRepo.ExistsAsync(Arg.Any<Expression<Func<GlobalGlossaryTerm, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.CreateTermAsync(NewCreateDto("sprint"), Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal("draft", result.Value!.Status);
        Assert.Equal("sprint", result.Value.Term);
        await _termsRepo.Received(1).AddAsync(
            Arg.Is<GlobalGlossaryTerm>(t => t.Term == "sprint" && t.Status == "draft"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishTermAsync_ReturnsNotFound_WhenTermDoesNotExist()
    {
        _termsRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((GlobalGlossaryTerm?)null);

        var result = await _service.PublishTermAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task PublishTermAsync_ReturnsFailure_WhenDefinitionMissing()
    {
        var termId = Guid.NewGuid();
        var term = new GlobalGlossaryTerm
        {
            Id = termId,
            Term = "architect",
            PreferredTranslation = "architect",
            Status = "draft",
            Definition = null,
        };
        _termsRepo.GetByIdAsync(termId, Arg.Any<CancellationToken>()).Returns(term);

        var result = await _service.PublishTermAsync(termId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("BAD_REQUEST", result.ErrorCode);
        Assert.Contains("definition", result.Error, StringComparison.OrdinalIgnoreCase);
        _termsRepo.DidNotReceive().Update(Arg.Any<GlobalGlossaryTerm>());
    }

    [Fact]
    public async Task PublishTermAsync_Succeeds_WhenDefinitionPresentAndBelowCap()
    {
        var termId = Guid.NewGuid();
        var term = new GlobalGlossaryTerm
        {
            Id = termId,
            Term = "architect",
            PreferredTranslation = "architect",
            Status = "draft",
            Definition = "A design role.",
        };
        _termsRepo.GetByIdAsync(termId, Arg.Any<CancellationToken>()).Returns(term);
        _termsRepo.CountAsync(Arg.Any<Expression<Func<GlobalGlossaryTerm, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(5);

        var result = await _service.PublishTermAsync(termId, Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal("published", term.Status);
        _termsRepo.Received(1).Update(Arg.Is<GlobalGlossaryTerm>(t => t.Id == termId && t.Status == "published"));
    }

    [Fact]
    public async Task PublishTermAsync_ReturnsFailure_WhenAtPublishedCapAndNotAlreadyPublished()
    {
        var termId = Guid.NewGuid();
        var term = new GlobalGlossaryTerm
        {
            Id = termId,
            Term = "architect",
            PreferredTranslation = "architect",
            Status = "draft",
            Definition = "A design role.",
        };
        _termsRepo.GetByIdAsync(termId, Arg.Any<CancellationToken>()).Returns(term);
        _termsRepo.CountAsync(Arg.Any<Expression<Func<GlobalGlossaryTerm, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(200);

        var result = await _service.PublishTermAsync(termId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("BAD_REQUEST", result.ErrorCode);
        Assert.Equal("draft", term.Status);
    }

    [Fact]
    public async Task ArchiveTermAsync_SetsStatusArchived()
    {
        var termId = Guid.NewGuid();
        var term = new GlobalGlossaryTerm
        {
            Id = termId,
            Term = "architect",
            PreferredTranslation = "architect",
            Status = "published",
        };
        _termsRepo.GetByIdAsync(termId, Arg.Any<CancellationToken>()).Returns(term);

        var result = await _service.ArchiveTermAsync(termId, Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal("archived", term.Status);
        _termsRepo.Received(1).Update(Arg.Is<GlobalGlossaryTerm>(t => t.Status == "archived"));
    }

    [Fact]
    public async Task ArchiveTermAsync_PublishesEmbeddingDeleteRequest_SoSearchStopsSurfacingIt()
    {
        // Archiving a published term must remove its vector from Qdrant — otherwise it keeps
        // showing up in semantic search results forever even though it's no longer "published".
        var termId = Guid.NewGuid();
        var term = new GlobalGlossaryTerm
        {
            Id = termId,
            Term = "architect",
            PreferredTranslation = "architect",
            Status = "published",
        };
        _termsRepo.GetByIdAsync(termId, Arg.Any<CancellationToken>()).Returns(term);

        await _service.ArchiveTermAsync(termId, Guid.NewGuid());

        await _redisDatabase.Received(1).StreamAddAsync(
            "embedding:index_requests",
            Arg.Is<NameValueEntry[]>(entries =>
                FieldValue(entries, "deletion_state") == "deleted" &&
                FieldValue(entries, "source_id") == termId.ToString() &&
                FieldValue(entries, "collection_id") == "global_glossary"),
            Arg.Any<RedisValue?>(),
            Arg.Any<long?>(),
            Arg.Any<bool>(),
            Arg.Any<long?>(),
            Arg.Any<StreamTrimMode>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task DeleteTermAsync_PublishesEmbeddingDeleteRequest_SoSearchStopsSurfacingIt()
    {
        var termId = Guid.NewGuid();
        var term = new GlobalGlossaryTerm
        {
            Id = termId,
            Term = "architect",
            PreferredTranslation = "architect",
            Status = "published",
            DeletedAt = null,
        };
        _termsRepo.GetByIdAsync(termId, Arg.Any<CancellationToken>()).Returns(term);

        await _service.DeleteTermAsync(termId, Guid.NewGuid());

        await _redisDatabase.Received(1).StreamAddAsync(
            "embedding:index_requests",
            Arg.Is<NameValueEntry[]>(entries =>
                FieldValue(entries, "deletion_state") == "deleted" &&
                FieldValue(entries, "source_id") == termId.ToString()),
            Arg.Any<RedisValue?>(),
            Arg.Any<long?>(),
            Arg.Any<bool>(),
            Arg.Any<long?>(),
            Arg.Any<StreamTrimMode>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task DeleteTermAsync_SoftDeletes_SetsDeletedAtAndDeletedBy()
    {
        var termId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var term = new GlobalGlossaryTerm
        {
            Id = termId,
            Term = "architect",
            PreferredTranslation = "architect",
            Status = "published",
            DeletedAt = null,
        };
        _termsRepo.GetByIdAsync(termId, Arg.Any<CancellationToken>()).Returns(term);

        var result = await _service.DeleteTermAsync(termId, actorId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(term.DeletedAt);
        Assert.Equal(actorId, term.DeletedBy);
        _termsRepo.Received(1).Update(Arg.Is<GlobalGlossaryTerm>(t => t.DeletedAt != null));
    }

    [Fact]
    public async Task DeleteTermAsync_ReturnsNotFound_WhenAlreadyDeleted()
    {
        var termId = Guid.NewGuid();
        var term = new GlobalGlossaryTerm
        {
            Id = termId,
            Term = "architect",
            PreferredTranslation = "architect",
            Status = "published",
            DeletedAt = DateTime.UtcNow,
        };
        _termsRepo.GetByIdAsync(termId, Arg.Any<CancellationToken>()).Returns(term);

        var result = await _service.DeleteTermAsync(termId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }
}
