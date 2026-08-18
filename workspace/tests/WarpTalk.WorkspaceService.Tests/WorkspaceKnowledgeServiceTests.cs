using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceKnowledge;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceKnowledgeServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceMemberRepository _memberRepository = Substitute.For<IWorkspaceMemberRepository>();
    private readonly IAuthIdentityClient _authIdentity = Substitute.For<IAuthIdentityClient>();
    private readonly IKnowledgeChunkReader _chunkReader = Substitute.For<IKnowledgeChunkReader>();
    private readonly IKnowledgeChunkWriter _chunkWriter = Substitute.For<IKnowledgeChunkWriter>();
    private readonly WorkspaceKnowledgeService _service;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _roleId = Guid.NewGuid();

    public WorkspaceKnowledgeServiceTests()
    {
        _unitOfWork.WorkspaceMemberRepository.Returns(_memberRepository);
        _service = new WorkspaceKnowledgeService(
            _unitOfWork,
            _authIdentity,
            _chunkReader,
            _chunkWriter,
            Substitute.For<ILogger<WorkspaceKnowledgeService>>());
    }

    private void GivenMemberWithRole(string roleName)
    {
        _memberRepository
            .FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember
            {
                WorkspaceId = _workspaceId,
                UserId = _userId,
                RoleId = _roleId,
            });
        _authIdentity.GetRoleByIdAsync(_roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _roleId, Name = roleName });
    }

    private void GivenNoMembership()
    {
        _memberRepository
            .FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);
    }

    private void GivenChunks(params KnowledgeChunkRecord[] records)
    {
        _chunkReader
            .ScrollAsync(
                Arg.Any<Guid>(),
                Arg.Any<KnowledgeChunkFilter>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new KnowledgeChunkPage(records, null));
    }

    private static KnowledgeChunkRecord DocumentChunk(string chunkId = "chunk-1") => new(
        ChunkId: chunkId,
        SourceType: "document",
        Text: "Payment terms are net 30.",
        Fact: "Payment terms are net 30",
        FactCategory: "requirement",
        DocumentId: Guid.NewGuid().ToString(),
        DocumentName: "contract.pdf",
        ChunkIndex: 3,
        SpeakerName: null,
        StartMs: null,
        RetentionState: "active",
        DeletionState: "active",
        AiRetrieval: true);

    [Fact]
    public async Task GetKnowledgeAsync_ReturnsForbidden_WhenCallerIsNotAMember()
    {
        GivenNoMembership();

        var result = await _service.GetKnowledgeAsync(
            _workspaceId, new GetWorkspaceKnowledgeQuery(), _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        // The store must not be touched at all — an authorization failure that still reads
        // is a leak waiting for a logging change to expose it.
        await _chunkReader.DidNotReceiveWithAnyArgs().ScrollAsync(default, default!, default, default);
    }

    [Fact]
    public async Task GetKnowledgeAsync_ReturnsForbidden_WhenCallerIsAPlainMember()
    {
        GivenMemberWithRole("Member");

        var result = await _service.GetKnowledgeAsync(
            _workspaceId, new GetWorkspaceKnowledgeQuery(), _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _chunkReader.DidNotReceiveWithAnyArgs().ScrollAsync(default, default!, default, default);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    public async Task GetKnowledgeAsync_ReturnsChunks_ForOwnerAndAdmin(string roleName)
    {
        GivenMemberWithRole(roleName);
        GivenChunks(DocumentChunk());

        var result = await _service.GetKnowledgeAsync(
            _workspaceId, new GetWorkspaceKnowledgeQuery(), _userId);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal("contract.pdf", item.DocumentName);
        Assert.Equal("Payment terms are net 30", item.Fact);
        Assert.Equal("requirement", item.FactCategory);
    }

    [Fact]
    public async Task GetKnowledgeAsync_ScrollsTheWorkspaceFromTheRoute_NotOneFromTheQuery()
    {
        GivenMemberWithRole("Owner");
        GivenChunks();

        await _service.GetKnowledgeAsync(_workspaceId, new GetWorkspaceKnowledgeQuery(), _userId);

        await _chunkReader.Received(1).ScrollAsync(
            _workspaceId,
            Arg.Any<KnowledgeChunkFilter>(),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetKnowledgeAsync_RejectsAnUnknownFactCategory()
    {
        GivenMemberWithRole("Owner");

        var result = await _service.GetKnowledgeAsync(
            _workspaceId,
            new GetWorkspaceKnowledgeQuery { FactCategory = "vibes" },
            _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task GetKnowledgeAsync_RejectsAnUnknownSourceType()
    {
        GivenMemberWithRole("Owner");

        var result = await _service.GetKnowledgeAsync(
            _workspaceId,
            new GetWorkspaceKnowledgeQuery { SourceType = "email" },
            _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Theory]
    [InlineData("meeting_summary")]
    [InlineData("glossary")]
    [InlineData("workspace_context")]
    public async Task GetKnowledgeAsync_AcceptsEverySourceTypeThisListingIsAbout(string sourceType)
    {
        GivenMemberWithRole("Owner");
        GivenChunks();

        var result = await _service.GetKnowledgeAsync(
            _workspaceId, new GetWorkspaceKnowledgeQuery { SourceType = sourceType }, _userId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task GetKnowledgeAsync_ExpandsGlossaryToBothProducersThatWriteIt()
    {
        // A workspace's own glossary (glossary_term) and the platform's (global_glossary_term)
        // are separate producers. Asking for one would return half a glossary with no sign
        // that the other half exists.
        GivenMemberWithRole("Owner");
        GivenChunks();

        await _service.GetKnowledgeAsync(
            _workspaceId, new GetWorkspaceKnowledgeQuery { SourceType = "glossary" }, _userId);

        await _chunkReader.Received(1).ScrollAsync(
            Arg.Any<Guid>(),
            Arg.Is<KnowledgeChunkFilter>(filter =>
                filter.SourceTypes != null &&
                filter.SourceTypes.Contains("glossary_term") &&
                filter.SourceTypes.Contains("global_glossary_term")),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetKnowledgeAsync_RejectsTranscriptAsAFilterBecauseItIsNeverListed()
    {
        // Transcript segments are excluded from this view (see ExcludedSourceTypes). Accepting
        // "transcript" as a filter would return an empty page and read as "this workspace has
        // no meetings" rather than "that is not what this page shows".
        GivenMemberWithRole("Owner");

        var result = await _service.GetKnowledgeAsync(
            _workspaceId,
            new GetWorkspaceKnowledgeQuery { SourceType = "transcript" },
            _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task GetKnowledgeAsync_AlwaysExcludesRawTranscriptSegments()
    {
        // The exclusion has to reach the store. Filtering after paging would turn a page of 50
        // into however few non-transcript rows it happened to contain, and a meeting-heavy
        // workspace would page through empty screens to find one document.
        GivenMemberWithRole("Owner");
        GivenChunks();

        await _service.GetKnowledgeAsync(_workspaceId, new GetWorkspaceKnowledgeQuery(), _userId);

        await _chunkReader.Received(1).ScrollAsync(
            Arg.Any<Guid>(),
            Arg.Is<KnowledgeChunkFilter>(filter =>
                filter.ExcludedSourceTypes != null &&
                filter.ExcludedSourceTypes.Contains("transcript")),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetKnowledgeAsync_ClampsAnOversizedPageAndFillsInAnAbsentOne()
    {
        GivenMemberWithRole("Owner");
        GivenChunks();

        await _service.GetKnowledgeAsync(
            _workspaceId, new GetWorkspaceKnowledgeQuery { PageSize = 5000 }, _userId);
        await _chunkReader.Received(1).ScrollAsync(
            Arg.Any<Guid>(), Arg.Any<KnowledgeChunkFilter>(), 100, Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await _service.GetKnowledgeAsync(
            _workspaceId, new GetWorkspaceKnowledgeQuery { PageSize = 0 }, _userId);
        await _chunkReader.Received(1).ScrollAsync(
            Arg.Any<Guid>(), Arg.Any<KnowledgeChunkFilter>(), 50, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetKnowledgeAsync_SurfacesAStoreFailureAsFiveHundred_NotAsAnEmptyWorkspace()
    {
        GivenMemberWithRole("Owner");
        _chunkReader
            .ScrollAsync(
                Arg.Any<Guid>(), Arg.Any<KnowledgeChunkFilter>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<KnowledgeChunkPage>>(_ => throw new HttpRequestExceptionStub());

        var result = await _service.GetKnowledgeAsync(
            _workspaceId, new GetWorkspaceKnowledgeQuery(), _userId);

        // "The vector store is down" and "you have indexed nothing" must not look the same to
        // an owner deciding whether their upload worked.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
    }

    // GetKnowledgeForAdminAsync and its test are gone with the admin knowledge endpoint:
    // tenant content stays out of the admin portal (2026-08-17).

    // ── Correcting and removing what was indexed ─────────────────────────────────────────
    //
    // The listing is Owner OR Admin; these are Owner only. The asymmetry is the point: seeing
    // what the assistant knows and deciding what it is allowed to know are different acts, and
    // the second one also erases the evidence of the first.

    private void GivenChunkExists(KnowledgeChunkRecord record)
    {
        _chunkReader
            .FindAsync(_workspaceId, record.ChunkId, Arg.Any<CancellationToken>())
            .Returns(record);
    }

    private static UpdateWorkspaceKnowledgeChunkRequest Update(
        string? fact = "Payment terms are net 45",
        string? category = "requirement",
        bool aiRetrieval = true)
        => new() { Fact = fact, FactCategory = category, AiRetrieval = aiRetrieval };

    [Fact]
    public async Task UpdateKnowledgeChunkAsync_IsRefusedForAnAdmin()
    {
        // An Admin can read this page. Rewriting what WarpBot will tell the workspace is a
        // different thing, and it is the Owner's.
        GivenMemberWithRole("Admin");

        var result = await _service.UpdateKnowledgeChunkAsync(
            _workspaceId, "chunk-1", Update(), _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _chunkWriter.DidNotReceiveWithAnyArgs().SetAnnotationAsync(default, default!, default!);
    }

    [Fact]
    public async Task DeleteKnowledgeChunkAsync_IsRefusedForAnAdmin()
    {
        GivenMemberWithRole("Admin");

        var result = await _service.DeleteKnowledgeChunkAsync(_workspaceId, "chunk-1", _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _chunkWriter.DidNotReceiveWithAnyArgs().DeleteAsync(default, default!);
    }

    [Fact]
    public async Task DeleteKnowledgeChunkAsync_ReadsBeforeItWrites()
    {
        // The tenancy check. Chunk ids are globally unique in a store shared by every
        // workspace, so an id in a URL proves nothing about who owns it — the read is what
        // turns "delete this id" into "delete this id IF it is ours".
        GivenMemberWithRole("Owner");
        _chunkReader
            .FindAsync(_workspaceId, "someone-elses-chunk", Arg.Any<CancellationToken>())
            .Returns((KnowledgeChunkRecord?)null);

        var result = await _service.DeleteKnowledgeChunkAsync(
            _workspaceId, "someone-elses-chunk", _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _chunkWriter.DidNotReceiveWithAnyArgs().DeleteAsync(default, default!);
    }

    [Fact]
    public async Task DeleteKnowledgeChunkAsync_RemovesAChunkTheWorkspaceOwns()
    {
        GivenMemberWithRole("Owner");
        GivenChunkExists(DocumentChunk());

        var result = await _service.DeleteKnowledgeChunkAsync(_workspaceId, "chunk-1", _userId);

        Assert.True(result.IsSuccess);
        await _chunkWriter.Received(1).DeleteAsync(_workspaceId, "chunk-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateKnowledgeChunkAsync_RejectsACategoryOutsideTheClosedSet()
    {
        // Rejected before the store is touched. The listing filters by category, so a value
        // nothing else recognises produces a row that no filter can ever show again.
        GivenMemberWithRole("Owner");

        var result = await _service.UpdateKnowledgeChunkAsync(
            _workspaceId, "chunk-1", Update(category: "vibes"), _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        await _chunkReader.DidNotReceiveWithAnyArgs().FindAsync(default, default!);
        await _chunkWriter.DidNotReceiveWithAnyArgs().SetAnnotationAsync(default, default!, default!);
    }

    [Fact]
    public async Task UpdateKnowledgeChunkAsync_RejectsACategoryWithNoFactToCategorise()
    {
        GivenMemberWithRole("Owner");

        var result = await _service.UpdateKnowledgeChunkAsync(
            _workspaceId, "chunk-1", Update(fact: "   ", category: "risk"), _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task UpdateKnowledgeChunkAsync_ClearsAFactWhenBothAreEmptied()
    {
        // "This extracted fact is wrong and I have nothing to put in its place" is a real
        // correction, and it must not be mistaken for "leave it as it was".
        GivenMemberWithRole("Owner");
        GivenChunkExists(DocumentChunk());

        var result = await _service.UpdateKnowledgeChunkAsync(
            _workspaceId, "chunk-1", Update(fact: null, category: null), _userId);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Fact);
        Assert.Null(result.Value.FactCategory);
        await _chunkWriter.Received(1).SetAnnotationAsync(
            _workspaceId,
            "chunk-1",
            Arg.Is<KnowledgeChunkAnnotation>(a => a.Fact == null && a.FactCategory == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateKnowledgeChunkAsync_WritesTheTrimmedFactAndReturnsIt()
    {
        GivenMemberWithRole("Owner");
        GivenChunkExists(DocumentChunk());

        var result = await _service.UpdateKnowledgeChunkAsync(
            _workspaceId,
            "chunk-1",
            Update(fact: "  Payment terms are net 45  ", category: "Requirement"),
            _userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("Payment terms are net 45", result.Value!.Fact);
        // Categories are stored lower-case, the same way the listing filter sends them.
        Assert.Equal("requirement", result.Value.FactCategory);
        // Everything the Owner does not get to change comes back unchanged.
        Assert.Equal("Payment terms are net 30.", result.Value.Text);
        Assert.Equal("contract.pdf", result.Value.DocumentName);
        await _chunkWriter.Received(1).SetAnnotationAsync(
            _workspaceId,
            "chunk-1",
            Arg.Is<KnowledgeChunkAnnotation>(a =>
                a.Fact == "Payment terms are net 45" && a.FactCategory == "requirement"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateKnowledgeChunkAsync_CanTakeAChunkOutOfRetrievalWithoutDeletingIt()
    {
        // The softer alternative to delete: the row stays on the page, auditable, and stops
        // being reachable in an answer.
        GivenMemberWithRole("Owner");
        GivenChunkExists(DocumentChunk());

        var result = await _service.UpdateKnowledgeChunkAsync(
            _workspaceId, "chunk-1", Update(aiRetrieval: false), _userId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.AiRetrieval);
        await _chunkWriter.DidNotReceiveWithAnyArgs().DeleteAsync(default, default!);
    }

    [Fact]
    public async Task UpdateKnowledgeChunkAsync_IsRefusedForSomeoneWhoIsNotInTheWorkspace()
    {
        GivenNoMembership();

        var result = await _service.UpdateKnowledgeChunkAsync(
            _workspaceId, "chunk-1", Update(), _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _chunkReader.DidNotReceiveWithAnyArgs().FindAsync(default, default!);
        await _chunkWriter.DidNotReceiveWithAnyArgs().SetAnnotationAsync(default, default!, default!);
    }

    private sealed class HttpRequestExceptionStub : Exception
    {
    }
}
