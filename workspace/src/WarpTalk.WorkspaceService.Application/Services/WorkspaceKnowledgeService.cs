using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceKnowledge;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceKnowledgeService : IWorkspaceKnowledgeService
{
    /// <summary>
    /// The same closed set the extractor writes
    /// (warptalk-ai/ai_assistant_worker/knowledge_facts.py FACT_CATEGORIES). Kept closed on
    /// this side too so an unknown value is rejected as a bad request rather than quietly
    /// matching nothing and looking like an empty workspace.
    /// </summary>
    private static readonly string[] FactCategories =
    [
        "decision", "requirement", "definition", "commitment", "risk", "reference",
    ];

    /// <summary>
    /// What this listing is about — durable knowledge a person would recognise as theirs —
    /// mapped from the name they use to the source types actually stored under it.
    ///
    /// The indirection exists because "glossary" is two producers, not one: GlossaryService
    /// writes <c>glossary_term</c> for a workspace's own glossary and GlobalGlossaryService
    /// writes <c>global_glossary_term</c> for the platform's. Exposing the stored names
    /// instead would make a caller ask for both to see their glossary, and asking for one
    /// would quietly return half of it.
    /// </summary>
    private static readonly Dictionary<string, string[]> SourceTypes = new(StringComparer.Ordinal)
    {
        ["document"] = ["document"],
        ["meeting_summary"] = ["meeting_summary"],
        ["glossary"] = ["glossary_term", "global_glossary_term"],
        ["workspace_context"] = ["workspace_context"],
    };

    /// <summary>
    /// Raw transcript segments are indexed one Qdrant point per sentence spoken
    /// (TranscriptRedisConsumerService publishes per segment, because no "transcript
    /// finalized" event exists to batch on). They are excellent retrieval material and
    /// terrible reading material: a one-hour meeting contributes hundreds of rows that say
    /// nothing on their own and bury every document the workspace uploaded.
    ///
    /// They stay indexed and stay searchable by WarpBot. They are excluded from THIS view
    /// only, and the exclusion is applied in the store rather than after paging — filtering
    /// a page of 50 down to the 2 non-transcript rows would make paging meaningless.
    /// </summary>
    private static readonly string[] ExcludedSourceTypes = ["transcript"];

    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IKnowledgeChunkReader _chunkReader;
    private readonly ILogger<WorkspaceKnowledgeService> _logger;

    public WorkspaceKnowledgeService(
        IUnitOfWork unitOfWork,
        IAuthIdentityClient authIdentity,
        IKnowledgeChunkReader chunkReader,
        ILogger<WorkspaceKnowledgeService> logger)
    {
        _unitOfWork = unitOfWork;
        _authIdentity = authIdentity;
        _chunkReader = chunkReader;
        _logger = logger;
    }

    public async Task<Result<WorkspaceKnowledgePageDto>> GetKnowledgeAsync(
        Guid workspaceId,
        GetWorkspaceKnowledgeQuery query,
        Guid userId,
        CancellationToken ct = default)
    {
        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member == null)
        {
            return Result.Failure<WorkspaceKnowledgePageDto>(
                WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
        }

        var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
        if (!roleName.IsOwnerOrAdmin())
        {
            // A plain member may well have uploaded one of these documents, but this view
            // shows the whole workspace's indexed content at once — including chunks from
            // documents whose access policies exclude them.
            return Result.Failure<WorkspaceKnowledgePageDto>(
                "Forbidden. Only workspace Owner/Admin can view indexed knowledge.",
                ErrorCodes.Forbidden);
        }

        return await ReadPageAsync(workspaceId, query, ct);
    }

    public Task<Result<WorkspaceKnowledgePageDto>> GetKnowledgeForAdminAsync(
        Guid workspaceId,
        GetWorkspaceKnowledgeQuery query,
        CancellationToken ct = default)
        => ReadPageAsync(workspaceId, query, ct);

    private async Task<Result<WorkspaceKnowledgePageDto>> ReadPageAsync(
        Guid workspaceId,
        GetWorkspaceKnowledgeQuery query,
        CancellationToken ct)
    {
        var sourceType = Normalize(query.SourceType);
        string[]? sourceTypes = null;
        if (sourceType != null && !SourceTypes.TryGetValue(sourceType, out sourceTypes))
        {
            return Result.Failure<WorkspaceKnowledgePageDto>(
                $"Unknown sourceType. Expected one of: {string.Join(", ", SourceTypes.Keys)}.",
                ErrorCodes.ValidationError);
        }

        var factCategory = Normalize(query.FactCategory);
        if (factCategory != null && !FactCategories.Contains(factCategory, StringComparer.Ordinal))
        {
            return Result.Failure<WorkspaceKnowledgePageDto>(
                $"Unknown factCategory. Expected one of: {string.Join(", ", FactCategories)}.",
                ErrorCodes.ValidationError);
        }

        var pageSize = query.PageSize <= 0 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize);

        try
        {
            var page = await _chunkReader.ScrollAsync(
                workspaceId,
                new KnowledgeChunkFilter(sourceTypes, factCategory, ExcludedSourceTypes),
                pageSize,
                string.IsNullOrWhiteSpace(query.Cursor) ? null : query.Cursor,
                ct);

            var items = page.Items.Select(record => new WorkspaceKnowledgeChunkDto(
                record.ChunkId,
                record.SourceType,
                record.Text,
                record.Fact,
                record.FactCategory,
                record.DocumentId,
                record.DocumentName,
                record.ChunkIndex,
                record.SpeakerName,
                record.StartMs,
                record.RetentionState,
                record.DeletionState,
                record.AiRetrieval,
                record.SourceTitle)).ToList();

            return Result.Success(new WorkspaceKnowledgePageDto(items, page.NextCursor));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Workspace knowledge listing failed. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<WorkspaceKnowledgePageDto>(
                "An unexpected error occurred while reading indexed knowledge.",
                ErrorCodes.InternalServerError);
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
