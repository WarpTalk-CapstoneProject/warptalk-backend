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

    [Fact]
    public async Task GetKnowledgeForAdminAsync_ConsultsNoMembership()
    {
        GivenNoMembership();
        GivenChunks(DocumentChunk());

        var result = await _service.GetKnowledgeForAdminAsync(
            _workspaceId, new GetWorkspaceKnowledgeQuery());

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Items);
        await _authIdentity.DidNotReceiveWithAnyArgs().GetRoleByIdAsync(default, default);
    }

    private sealed class HttpRequestExceptionStub : Exception
    {
    }
}
