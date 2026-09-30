using System;
using System.Data.Common;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// WT-699 / TC2804 — a second glossary with the same name in one workspace is a conflict the
/// caller can fix, not a server error.
///
/// (workspace_id, name) is unique in the database, and CreateGlossaryAsync used to let the INSERT
/// hit that index, catch the exception in its catch-all and answer INTERNAL_ERROR — a 500 that
/// told the person picking a name that the server had broken.
/// </summary>
public class GlossaryDuplicateNameTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGlossaryRepository _glossaries = Substitute.For<IGlossaryRepository>();
    private readonly GlossaryService _service;

    public GlossaryDuplicateNameTests()
    {
        _unitOfWork.Glossaries.Returns(_glossaries);
        _unitOfWork.GlossaryTerms.Returns(Substitute.For<IGlossaryTermRepository>());

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase().ReturnsForAnyArgs(Substitute.For<IDatabase>());

        _service = new GlossaryService(
            _unitOfWork,
            Substitute.For<ILogger<GlossaryService>>(),
            redis);
    }

    private static CreateGlossaryDto Request(Guid workspaceId) =>
        new(workspaceId, "Aviation", "Terms for flight ops", "en", "vi");

    [Fact]
    public async Task Creating_A_Glossary_Whose_Name_Is_Taken_Is_A_Conflict_Not_An_Internal_Error()
    {
        _glossaries
            .ExistsAsync(Arg.Any<Expression<Func<Glossary, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.CreateGlossaryAsync(Request(Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("CONFLICT", result.ErrorCode);
        Assert.Contains("Aviation", result.Error);
        await _glossaries.DidNotReceive().AddAsync(Arg.Any<Glossary>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Two creates can both pass the up-front check; the database's unique index is what refuses
    /// the second. That refusal must arrive as the same CONFLICT, not fall into the catch-all.
    /// </summary>
    [Fact]
    public async Task A_Unique_Violation_From_A_Racing_Create_Is_Also_A_Conflict()
    {
        _unitOfWork
            .SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("save failed", new UniqueViolation()));

        var result = await _service.CreateGlossaryAsync(Request(Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("CONFLICT", result.ErrorCode);
    }

    [Fact]
    public async Task Renaming_Onto_Another_Glossarys_Name_Is_A_Conflict()
    {
        var id = Guid.NewGuid();
        _glossaries.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(new Glossary { Id = id, WorkspaceId = Guid.NewGuid(), Name = "Old name" });
        _glossaries
            .ExistsAsync(Arg.Any<Expression<Func<Glossary, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.UpdateGlossaryAsync(id, new UpdateGlossaryDto("Aviation", null, true));

        Assert.False(result.IsSuccess);
        Assert.Equal("CONFLICT", result.ErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private sealed class UniqueViolation : DbException
    {
        public override string SqlState => "23505";
    }
}
