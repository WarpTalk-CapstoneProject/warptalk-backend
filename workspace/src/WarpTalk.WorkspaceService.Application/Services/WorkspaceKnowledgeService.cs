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

    /// <summary>A fact is one line about one chunk. Past this it is a second copy of the text.</summary>
    private const int MaxFactLength = 500;

    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IKnowledgeChunkReader _chunkReader;
    private readonly IKnowledgeChunkWriter _chunkWriter;
    private readonly ILogger<WorkspaceKnowledgeService> _logger;

    public WorkspaceKnowledgeService(
        IUnitOfWork unitOfWork,
        IAuthIdentityClient authIdentity,
        IKnowledgeChunkReader chunkReader,
        IKnowledgeChunkWriter chunkWriter,
        ILogger<WorkspaceKnowledgeService> logger)
    {
        _unitOfWork = unitOfWork;
        _authIdentity = authIdentity;
        _chunkReader = chunkReader;
        _chunkWriter = chunkWriter;
        _logger = logger;
    }

    public async Task<Result<WorkspaceKnowledgePageDto>> GetKnowledgeAsync(
        Guid workspaceId,
        GetWorkspaceKnowledgeQuery query,
        Guid userId,
        CancellationToken ct = default)
    {
        // A plain member may well have uploaded one of these documents, but this view shows the
        // whole workspace's indexed content at once — including chunks from documents whose
        // access policies exclude them.
        var role = await ResolveRoleAsync(workspaceId, userId, ct);
        if (!role.IsSuccess)
        {
            return Result.Failure<WorkspaceKnowledgePageDto>(role.Error!, role.ErrorCode);
        }
        if (!role.Value.IsOwnerOrAdmin())
        {
            return Result.Failure<WorkspaceKnowledgePageDto>(
                "Forbidden. Only workspace Owner/Admin can view indexed knowledge.",
                ErrorCodes.Forbidden);
        }

        return await ReadPageAsync(workspaceId, query, ct);
    }

    public async Task<Result<WorkspaceKnowledgeChunkDto>> UpdateKnowledgeChunkAsync(
        Guid workspaceId,
        string chunkId,
        UpdateWorkspaceKnowledgeChunkRequest request,
        Guid userId,
        CancellationToken ct = default)
    {
        var owner = await RequireOwnerAsync(workspaceId, userId, ct);
        if (!owner.IsSuccess)
        {
            return Result.Failure<WorkspaceKnowledgeChunkDto>(owner.Error!, owner.ErrorCode);
        }

        var factCategory = Normalize(request.FactCategory);
        if (factCategory != null && !FactCategories.Contains(factCategory, StringComparer.Ordinal))
        {
            return Result.Failure<WorkspaceKnowledgeChunkDto>(
                $"Unknown factCategory. Expected one of: {string.Join(", ", FactCategories)}.",
                ErrorCodes.ValidationError);
        }

        var fact = string.IsNullOrWhiteSpace(request.Fact) ? null : request.Fact.Trim();
        if (fact != null && fact.Length > MaxFactLength)
        {
            return Result.Failure<WorkspaceKnowledgeChunkDto>(
                $"A fact must be {MaxFactLength} characters or fewer.",
                ErrorCodes.ValidationError);
        }

        // A category with nothing to categorise is a filter chip pointing at an empty row: the
        // listing groups by category, so this would put a blank line under "Decision".
        if (fact == null && factCategory != null)
        {
            return Result.Failure<WorkspaceKnowledgeChunkDto>(
                "A fact category needs a fact. Clear the category as well, or write the fact.",
                ErrorCodes.ValidationError);
        }

        try
        {
            // Read before write, always. This is the only thing standing between a chunk id in
            // a URL and another workspace's index — the store is shared and ids are globally
            // unique, so an id alone proves nothing about who it belongs to.
            var existing = await _chunkReader.FindAsync(workspaceId, chunkId, ct);
            if (existing == null)
            {
                return Result.Failure<WorkspaceKnowledgeChunkDto>(
                    "No such indexed chunk in this workspace.", ErrorCodes.NotFound);
            }

            await _chunkWriter.SetAnnotationAsync(
                workspaceId,
                chunkId,
                new KnowledgeChunkAnnotation(fact, factCategory, request.AiRetrieval),
                ct);

            // Returned from what was just written rather than by re-reading. Qdrant's payload
            // update is applied with wait=true, but a second read would still be a second
            // chance to answer with a stale page from a replica — and the caller's screen is
            // showing the row it just edited.
            var updated = existing with
            {
                Fact = fact,
                FactCategory = factCategory,
                AiRetrieval = request.AiRetrieval,
            };

            _logger.LogInformation(
                "Knowledge chunk annotated by owner. WorkspaceId: {WorkspaceId}, ChunkId: {ChunkId}, Retrievable: {AiRetrieval}",
                workspaceId, chunkId, request.AiRetrieval);

            return Result.Success(ToDto(updated));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Knowledge chunk update failed. WorkspaceId: {WorkspaceId}, ChunkId: {ChunkId}",
                workspaceId, chunkId);
            return Result.Failure<WorkspaceKnowledgeChunkDto>(
                "An unexpected error occurred while updating the indexed chunk.",
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<bool>> DeleteKnowledgeChunkAsync(
        Guid workspaceId,
        string chunkId,
        Guid userId,
        CancellationToken ct = default)
    {
        var owner = await RequireOwnerAsync(workspaceId, userId, ct);
        if (!owner.IsSuccess)
        {
            return Result.Failure<bool>(owner.Error!, owner.ErrorCode);
        }

        try
        {
            var existing = await _chunkReader.FindAsync(workspaceId, chunkId, ct);
            if (existing == null)
            {
                // Already gone, or never this workspace's. Both are 404 — telling the caller
                // which would confirm the existence of another workspace's chunk.
                return Result.Failure<bool>(
                    "No such indexed chunk in this workspace.", ErrorCodes.NotFound);
            }

            await _chunkWriter.DeleteAsync(workspaceId, chunkId, ct);

            _logger.LogInformation(
                "Knowledge chunk deleted by owner. WorkspaceId: {WorkspaceId}, ChunkId: {ChunkId}, SourceType: {SourceType}",
                workspaceId, chunkId, existing.SourceType);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Knowledge chunk delete failed. WorkspaceId: {WorkspaceId}, ChunkId: {ChunkId}",
                workspaceId, chunkId);
            return Result.Failure<bool>(
                "An unexpected error occurred while deleting the indexed chunk.",
                ErrorCodes.InternalServerError);
        }
    }

    /// <summary>The caller's role in this workspace, or Forbidden if they are not in it.</summary>
    private async Task<Result<string?>> ResolveRoleAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct)
    {
        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member == null)
        {
            return Result.Failure<string?>(
                WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
        }

        return Result.Success<string?>(await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct));
    }

    private async Task<Result<bool>> RequireOwnerAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct)
    {
        var role = await ResolveRoleAsync(workspaceId, userId, ct);
        if (!role.IsSuccess) return Result.Failure<bool>(role.Error!, role.ErrorCode);

        if (!role.Value.IsOwner())
        {
            return Result.Failure<bool>(
                "Forbidden. Only the workspace Owner can change what has been indexed.",
                ErrorCodes.Forbidden);
        }

        return Result.Success(true);
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

            var items = page.Items.Select(ToDto).ToList();

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

    private static WorkspaceKnowledgeChunkDto ToDto(KnowledgeChunkRecord record)
        => new(
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
            record.SourceTitle,
            record.IndexedAtMs);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
