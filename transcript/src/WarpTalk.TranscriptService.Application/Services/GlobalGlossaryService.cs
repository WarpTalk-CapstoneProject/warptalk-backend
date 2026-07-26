using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Application.Services;

/// <summary>
/// Admin-only CRUD + publish/archive lifecycle for the system-managed global glossary. Every
/// mutation writes a transcript.global_glossary_audits row — see docs/global-glossary-plan.md
/// §3.4/§6: with blast radius covering every workspace, "who changed what, when" cannot be
/// reconstructed after the fact from the term row alone (it only holds the current state).
/// </summary>
public class GlobalGlossaryService : IGlobalGlossaryService
{
    private const int MaxPublishedTerms = 200; // §6: bounds prompt-injection risk from an unbounded term list.

    // Sentinel workspace_id tag on global-glossary vectors — EmbeddingSearchWorker.process
    // (warptalk-ai) hard-filters vector search on {"workspace_id": <request.workspace_id>}, so
    // the same constant must be used both when indexing (here) and when querying the
    // "global_glossary" collection (chat_tools._semantic_search) or the filter excludes every
    // row. See docs/global-glossary-plan.md §5.4.
    private const string GlobalCollectionId = "global_glossary";
    private const string GlobalWorkspaceSentinel = "global";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<GlobalGlossaryService> _logger;
    private readonly IConnectionMultiplexer _redis;

    public GlobalGlossaryService(IUnitOfWork unitOfWork, ILogger<GlobalGlossaryService> logger, IConnectionMultiplexer redis)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _redis = redis;
    }

    public async Task<Result<PagedResultDto<GlobalGlossaryTermDto>>> GetTermsAsync(GlobalGlossaryTermQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = Math.Max(1, query.Page);
            var pageSize = Math.Clamp(query.PageSize, 1, 100);

            var terms = await _unitOfWork.GlobalGlossaryTerms.GetPagedAsync(
                t => t.DeletedAt == null
                    && (query.Status == null || t.Status == query.Status)
                    && (query.BusinessDomain == null || t.BusinessDomain == query.BusinessDomain)
                    && (query.Language == null || t.SourceLanguage == query.Language || t.TargetLanguage == query.Language)
                    && (query.Search == null || t.Term.Contains(query.Search) || t.PreferredTranslation.Contains(query.Search)),
                (page - 1) * pageSize,
                pageSize,
                q => q.OrderByDescending(t => t.Priority).ThenByDescending(t => t.CreatedAt),
                cancellationToken);

            var totalCount = await _unitOfWork.GlobalGlossaryTerms.CountAsync(
                t => t.DeletedAt == null
                    && (query.Status == null || t.Status == query.Status)
                    && (query.BusinessDomain == null || t.BusinessDomain == query.BusinessDomain)
                    && (query.Language == null || t.SourceLanguage == query.Language || t.TargetLanguage == query.Language)
                    && (query.Search == null || t.Term.Contains(query.Search) || t.PreferredTranslation.Contains(query.Search)),
                cancellationToken);

            return Result.Success(new PagedResultDto<GlobalGlossaryTermDto>(terms.Select(t => t.ToDto()), page, pageSize, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing global glossary terms");
            return Result.Failure<PagedResultDto<GlobalGlossaryTermDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<GlobalGlossaryTermDto>> GetTermByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = await _unitOfWork.GlobalGlossaryTerms.GetByIdAsync(id, cancellationToken);
            if (term == null || term.DeletedAt != null)
                return Result.Failure<GlobalGlossaryTermDto>($"Global glossary term {id} not found.", "NOT_FOUND");

            return Result.Success(term.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global glossary term {TermId}", id);
            return Result.Failure<GlobalGlossaryTermDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<GlobalGlossaryTermDto>> CreateTermAsync(CreateGlobalGlossaryTermDto dto, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await IsDuplicateAsync(dto.Term, dto.SourceLanguage, dto.TargetLanguage, dto.BusinessDomain, null, cancellationToken))
                return Result.Failure<GlobalGlossaryTermDto>(
                    "A term with the same (term, source language, target language, business domain) already exists.", "BAD_REQUEST");

            var term = dto.ToEntity(actorUserId);

            await _unitOfWork.GlobalGlossaryTerms.AddAsync(term, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await WriteAuditAsync(term.Id, "created", before: null, after: term, actorUserId, cancellationToken);

            return Result.Success(term.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating global glossary term");
            return Result.Failure<GlobalGlossaryTermDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> UpdateTermAsync(Guid id, UpdateGlobalGlossaryTermDto dto, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = await _unitOfWork.GlobalGlossaryTerms.GetByIdAsync(id, cancellationToken);
            if (term == null || term.DeletedAt != null)
                return Result.Failure($"Global glossary term {id} not found.", "NOT_FOUND");

            if (await IsDuplicateAsync(dto.Term, dto.SourceLanguage, dto.TargetLanguage, dto.BusinessDomain, id, cancellationToken))
                return Result.Failure(
                    "A term with the same (term, source language, target language, business domain) already exists.", "BAD_REQUEST");

            var before = CloneForAudit(term);

            term.Term = dto.Term;
            term.PreferredTranslation = dto.PreferredTranslation;
            term.SourceLanguage = dto.SourceLanguage;
            term.TargetLanguage = dto.TargetLanguage;
            term.BusinessDomain = dto.BusinessDomain;
            term.Definition = dto.Definition;
            term.UsageNote = dto.UsageNote;
            term.Priority = dto.Priority;
            term.Version += 1;
            term.UpdatedAt = DateTime.UtcNow;
            term.UpdatedBy = actorUserId;

            _unitOfWork.GlobalGlossaryTerms.Update(term);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await WriteAuditAsync(term.Id, "updated", before, term, actorUserId, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating global glossary term {TermId}", id);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> DeleteTermAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = await _unitOfWork.GlobalGlossaryTerms.GetByIdAsync(id, cancellationToken);
            if (term == null || term.DeletedAt != null)
                return Result.Failure($"Global glossary term {id} not found.", "NOT_FOUND");

            var before = CloneForAudit(term);

            // Soft delete only — the unique dedup index and the published-terms index both
            // already filter on `deleted_at IS NULL`, and a hard delete would destroy the audit
            // trail's ability to show what the term looked like before removal.
            term.DeletedAt = DateTime.UtcNow;
            term.DeletedBy = actorUserId;
            term.UpdatedAt = DateTime.UtcNow;
            term.UpdatedBy = actorUserId;

            _unitOfWork.GlobalGlossaryTerms.Update(term);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await WriteAuditAsync(term.Id, "deleted", before, term, actorUserId, cancellationToken);
            await TryPublishEmbeddingDeleteRequestAsync(term.Id, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting global glossary term {TermId}", id);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> PublishTermAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = await _unitOfWork.GlobalGlossaryTerms.GetByIdAsync(id, cancellationToken);
            if (term == null || term.DeletedAt != null)
                return Result.Failure($"Global glossary term {id} not found.", "NOT_FOUND");

            // §6: force every published term to explain itself — cuts down on junk/ambiguous
            // terms and doubles as RAG-ready content.
            if (string.IsNullOrWhiteSpace(term.Definition))
                return Result.Failure("A definition is required before a global glossary term can be published.", "BAD_REQUEST");

            if (term.Status != "published")
            {
                var publishedCount = await _unitOfWork.GlobalGlossaryTerms.CountAsync(
                    t => t.Status == "published" && t.DeletedAt == null, cancellationToken);
                if (publishedCount >= MaxPublishedTerms)
                    return Result.Failure(
                        $"The platform already has {MaxPublishedTerms} published global glossary terms (the configured cap). Archive an existing term before publishing another.",
                        "BAD_REQUEST");
            }

            var before = CloneForAudit(term);

            term.Status = "published";
            term.UpdatedAt = DateTime.UtcNow;
            term.UpdatedBy = actorUserId;

            _unitOfWork.GlobalGlossaryTerms.Update(term);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await WriteAuditAsync(term.Id, "published", before, term, actorUserId, cancellationToken);
            await TryPublishEmbeddingIndexRequestAsync(term, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing global glossary term {TermId}", id);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> ArchiveTermAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = await _unitOfWork.GlobalGlossaryTerms.GetByIdAsync(id, cancellationToken);
            if (term == null || term.DeletedAt != null)
                return Result.Failure($"Global glossary term {id} not found.", "NOT_FOUND");

            var before = CloneForAudit(term);

            term.Status = "archived";
            term.UpdatedAt = DateTime.UtcNow;
            term.UpdatedBy = actorUserId;

            _unitOfWork.GlobalGlossaryTerms.Update(term);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await WriteAuditAsync(term.Id, "archived", before, term, actorUserId, cancellationToken);
            await TryPublishEmbeddingDeleteRequestAsync(term.Id, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error archiving global glossary term {TermId}", id);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<BulkImportResultDto>> BulkImportAsync(BulkImportGlobalGlossaryTermsDto dto, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var imported = 0;
        var skipped = 0;

        try
        {
            foreach (var row in dto.Rows)
            {
                if (string.IsNullOrWhiteSpace(row.Term) || row.Term.Trim().Length < 3)
                {
                    skipped++;
                    errors.Add($"\"{row.Term}\": term must be at least 3 characters — skipped.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.PreferredTranslation))
                {
                    skipped++;
                    errors.Add($"\"{row.Term}\": preferredTranslation is required — skipped.");
                    continue;
                }

                if (await IsDuplicateAsync(row.Term, row.SourceLanguage, row.TargetLanguage, row.BusinessDomain, null, cancellationToken))
                {
                    skipped++;
                    errors.Add($"\"{row.Term}\": duplicate of an existing term — skipped.");
                    continue;
                }

                var term = new CreateGlobalGlossaryTermDto(
                    row.Term, row.PreferredTranslation, row.SourceLanguage, row.TargetLanguage,
                    row.BusinessDomain, row.Definition, row.UsageNote, row.Priority).ToEntity(actorUserId);

                await _unitOfWork.GlobalGlossaryTerms.AddAsync(term, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await WriteAuditAsync(term.Id, "created", before: null, after: term, actorUserId, cancellationToken);
                imported++;
            }

            return Result.Success(new BulkImportResultDto(imported, skipped, errors));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk-importing global glossary terms");
            return Result.Failure<BulkImportResultDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IReadOnlyList<GlobalGlossaryAuditDto>>> GetAuditsAsync(Guid termId, CancellationToken cancellationToken = default)
    {
        try
        {
            var audits = await _unitOfWork.GlobalGlossaryAudits.FindAsync(a => a.TermId == termId, cancellationToken);
            IReadOnlyList<GlobalGlossaryAuditDto> ordered = audits
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => a.ToDto())
                .ToList();

            return Result.Success(ordered);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting audits for global glossary term {TermId}", termId);
            return Result.Failure<IReadOnlyList<GlobalGlossaryAuditDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    /// <summary>
    /// Wires a newly-published global term into the RAG pipeline via the same
    /// "embedding:index_requests" Redis Stream GlossaryService uses for workspace glossary terms
    /// — but into its own "global_glossary" collection (not "workspace_{id}") so a single publish
    /// doesn't fan out into every workspace's collection. workspace_id is tagged with the
    /// GlobalWorkspaceSentinel constant because EmbeddingSearchWorker.process (warptalk-ai)
    /// unconditionally filters vector search on workspace_id — the read side
    /// (chat_tools._semantic_search) must query with that same sentinel. See
    /// docs/global-glossary-plan.md §5.4. A publish failure must not fail the admin action it
    /// rides along with, so it's swallowed here (logged only).
    /// </summary>
    private async Task TryPublishEmbeddingIndexRequestAsync(GlobalGlossaryTerm term, CancellationToken ct)
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(term.Definition)
                ? $"{term.Term} → {term.PreferredTranslation}"
                : $"{term.Term} → {term.PreferredTranslation}: {term.Definition}";

            var chunk = new
            {
                id = term.Id.ToString(),
                text,
                metadata = new
                {
                    global_glossary_term_id = term.Id.ToString(),
                    term = term.Term,
                    preferred_translation = term.PreferredTranslation,
                    business_domain = term.BusinessDomain,
                },
            };

            var entries = new NameValueEntry[]
            {
                new("job_id", Guid.NewGuid().ToString()),
                new("workspace_id", GlobalWorkspaceSentinel),
                new("collection_id", GlobalCollectionId),
                new("source_type", "global_glossary_term"),
                new("source_id", term.Id.ToString()),
                new("chunks_json", JsonSerializer.Serialize(new[] { chunk })),
                new("external_llm_allowed", "true"),
                new("ai_retrieval_allowed", "true"),
                new("retention_state", "active"),
                new("deletion_state", "active"),
                new("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            };

            var db = _redis.GetDatabase();
            await db.StreamAddAsync("embedding:index_requests", entries, maxLength: 10000, useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish embedding index request for global glossary term {TermId}", term.Id);
        }
    }

    /// <summary>
    /// Removes a term's vector from the "global_glossary" Qdrant collection via
    /// EmbeddingWorker.process's explicit deletion_state="deleted" path (warptalk-ai/
    /// embedding_worker/worker.py) — called on both archive and (soft) delete, since either
    /// one means the term should stop being surfaced by semantic search. Safe to call even
    /// for a term that was never published (never indexed): Qdrant delete-by-id is a no-op
    /// when the id isn't present, and EmbeddingWorker/QdrantVectorStore.delete treat a
    /// missing collection the same way. A publish failure must not fail the admin action it
    /// rides along with, so it's swallowed here (logged only).
    /// </summary>
    private async Task TryPublishEmbeddingDeleteRequestAsync(Guid termId, CancellationToken ct)
    {
        try
        {
            var chunk = new { id = termId.ToString(), text = "", metadata = new { } };

            var entries = new NameValueEntry[]
            {
                new("job_id", Guid.NewGuid().ToString()),
                new("workspace_id", GlobalWorkspaceSentinel),
                new("collection_id", GlobalCollectionId),
                new("source_type", "global_glossary_term"),
                new("source_id", termId.ToString()),
                new("chunks_json", JsonSerializer.Serialize(new[] { chunk })),
                new("external_llm_allowed", "true"),
                new("ai_retrieval_allowed", "true"),
                new("retention_state", "active"),
                new("deletion_state", "deleted"),
                new("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            };

            var db = _redis.GetDatabase();
            await db.StreamAddAsync("embedding:index_requests", entries, maxLength: 10000, useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish embedding delete request for global glossary term {TermId}", termId);
        }
    }

    private async Task<bool> IsDuplicateAsync(
        string term, string? sourceLanguage, string? targetLanguage, string? businessDomain, Guid? excludeId, CancellationToken ct)
    {
        var normalized = term.Trim().ToLowerInvariant();
        return await _unitOfWork.GlobalGlossaryTerms.ExistsAsync(
            t => t.DeletedAt == null
                && (excludeId == null || t.Id != excludeId)
                && t.Term.ToLower() == normalized
                && t.SourceLanguage == sourceLanguage
                && t.TargetLanguage == targetLanguage
                && t.BusinessDomain == businessDomain,
            ct);
    }

    private async Task WriteAuditAsync(Guid termId, string action, GlobalGlossaryAuditSnapshot? before, GlobalGlossaryTerm after, Guid actorUserId, CancellationToken ct)
    {
        try
        {
            var audit = new GlobalGlossaryAudit
            {
                Id = Guid.NewGuid(),
                TermId = termId,
                Action = action,
                BeforeJson = before == null ? null : JsonSerializer.Serialize(before),
                AfterJson = JsonSerializer.Serialize(CloneForAudit(after)),
                ActorUserId = actorUserId,
                CreatedAt = DateTime.UtcNow,
            };

            await _unitOfWork.GlobalGlossaryAudits.AddAsync(audit, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Auditing must never block the mutation it's recording — logged only.
            _logger.LogWarning(ex, "Failed to write global glossary audit for term {TermId}, action {Action}", termId, action);
        }
    }

    private static GlobalGlossaryAuditSnapshot CloneForAudit(GlobalGlossaryTerm term) => new(
        term.Id,
        term.Term,
        term.PreferredTranslation,
        term.SourceLanguage,
        term.TargetLanguage,
        term.BusinessDomain,
        term.Definition,
        term.UsageNote,
        term.Priority,
        term.Status,
        term.Version
    );

    /// <summary>Stable, decoupled-from-entity shape for before/after audit JSON — serializing the
    /// tracked GlobalGlossaryTerm directly would tie the audit schema to EF's entity shape.</summary>
    private record GlobalGlossaryAuditSnapshot(
        Guid Id, string Term, string PreferredTranslation, string? SourceLanguage, string? TargetLanguage,
        string? BusinessDomain, string? Definition, string? UsageNote, int Priority, string Status, int Version);
}
