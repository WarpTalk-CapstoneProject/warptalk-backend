using System;
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
/// WT-558 — creating a glossary answers with the glossary.
///
/// It used to answer a bare 201 with no body. A client that had just created a glossary therefore
/// had no id for it, and the only way to find one was to re-list the workspace and guess which
/// entry was new by matching the name — which is wrong the moment two glossaries share a name.
///
/// The ticket asks for term rows inside the create dialog, and that is not buildable on a guess:
/// the terms have to be posted to a glossary id the client can only get from this response.
/// </summary>
public class GlossaryCreateReturnsTheRowTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGlossaryRepository _glossaries = Substitute.For<IGlossaryRepository>();
    private readonly GlossaryService _service;

    public GlossaryCreateReturnsTheRowTests()
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
    public async Task Creating_A_Glossary_Returns_It_With_An_Id_The_Caller_Can_Use()
    {
        var workspaceId = Guid.NewGuid();

        var result = await _service.CreateGlossaryAsync(Request(workspaceId));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.NotEqual(Guid.Empty, result.Value!.Id);
        Assert.Equal(workspaceId, result.Value.WorkspaceId);
        Assert.Equal("Aviation", result.Value.Name);
        Assert.Equal("en", result.Value.SourceLanguage);
        Assert.Equal("vi", result.Value.TargetLanguage);
    }

    /// <summary>
    /// The id in the response must be the id of the row that was actually inserted. Returning a
    /// freshly minted Guid that the repository never saw would look correct in every assertion
    /// above and send the caller's terms to a glossary that does not exist.
    /// </summary>
    [Fact]
    public async Task The_Returned_Id_Is_The_Id_Of_The_Row_That_Was_Saved()
    {
        Glossary? inserted = null;
        await _glossaries.AddAsync(
            Arg.Do<Glossary>(glossary => inserted = glossary),
            Arg.Any<CancellationToken>());

        var result = await _service.CreateGlossaryAsync(Request(Guid.NewGuid()));

        Assert.NotNull(inserted);
        Assert.Equal(inserted!.Id, result.Value!.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_New_Glossary_Starts_Empty()
    {
        var result = await _service.CreateGlossaryAsync(Request(Guid.NewGuid()));

        Assert.Equal(0, result.Value!.TermCount);
        Assert.True(result.Value.IsActive);
    }
}
