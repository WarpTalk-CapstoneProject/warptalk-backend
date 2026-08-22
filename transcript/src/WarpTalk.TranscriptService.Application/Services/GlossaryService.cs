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

public class GlossaryService : IGlossaryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<GlossaryService> _logger;
    private readonly IConnectionMultiplexer _redis;

    public GlossaryService(IUnitOfWork unitOfWork, ILogger<GlossaryService> logger, IConnectionMultiplexer redis)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _redis = redis;
    }

    public async Task<Result<GlossaryDto>> CreateGlossaryAsync(CreateGlossaryDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = dto.ToEntity();

            await _unitOfWork.Glossaries.AddAsync(glossary, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // WT-558: the created row, not a bare acknowledgement. The id is assigned by ToEntity
            // before the insert, so this costs no extra read — and without it a client that has
            // just created a glossary cannot name it, which is what made "add a term while
            // creating" unbuildable.
            return Result.Success(glossary.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating glossary for workspace {WorkspaceId}", dto.WorkspaceId);
            return Result.Failure<GlossaryDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<GlossaryDto>> GetGlossaryByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(id, cancellationToken);
            if (glossary == null)
                return Result.Failure<GlossaryDto>($"Glossary with ID {id} not found.", "NOT_FOUND");

            return Result.Success(glossary.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting glossary {GlossaryId}", id);
            return Result.Failure<GlossaryDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<GlossaryDto>>> GetGlossariesByWorkspaceIdAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossaries = await _unitOfWork.Glossaries.FindAsync(g => g.WorkspaceId == workspaceId, cancellationToken);
            return Result.Success(glossaries.Select(g => g.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting glossaries for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<IEnumerable<GlossaryDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> UpdateGlossaryAsync(Guid id, UpdateGlossaryDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(id, cancellationToken);
            if (glossary == null)
                return Result.Failure<GlossaryDto>($"Glossary with ID {id} not found.", "NOT_FOUND");

            glossary.Name = dto.Name;
            glossary.Description = dto.Description;
            glossary.IsActive = dto.IsActive;
            glossary.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.Glossaries.Update(glossary);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating glossary {GlossaryId}", id);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> DeleteGlossaryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(id, cancellationToken);
            if (glossary == null)
                return Result.Failure($"Glossary with ID {id} not found.", "NOT_FOUND");

            var terms = await _unitOfWork.GlossaryTerms.FindAsync(t => t.GlossaryId == id, cancellationToken);
            var termIds = terms.Select(t => t.Id).ToList();

            _unitOfWork.Glossaries.Remove(glossary);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            foreach (var termId in termIds)
            {
                await TryPublishEmbeddingDeleteRequestAsync(glossary.WorkspaceId, termId, cancellationToken);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting glossary {GlossaryId}", id);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> AddTermAsync(Guid glossaryId, CreateGlossaryTermDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(glossaryId, cancellationToken);
            if (glossary == null)
                return Result.Failure($"Glossary with ID {glossaryId} not found.", "NOT_FOUND");

            var term = dto.ToEntity(glossaryId);

            await _unitOfWork.GlossaryTerms.AddAsync(term, cancellationToken);

            glossary.TermCount++;
            glossary.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.Glossaries.Update(glossary);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await TryPublishEmbeddingIndexRequestAsync(glossary.WorkspaceId, term, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding term to glossary {GlossaryId}", glossaryId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    /// <summary>
    /// WT-472: import many terms in one transaction.
    ///
    /// WHY NOT LOOP AddTermAsync. That would be a SaveChanges and a TermCount increment per term,
    /// so a client that died halfway would leave the counter describing a glossary that does not
    /// exist — and 100 terms would be 100 round trips. Here the rows are added, the counter is
    /// adjusted ONCE by however many actually landed, and everything commits together.
    ///
    /// DUPLICATES ARE SKIPPED, NOT REJECTED, and the count is reported. An import is usually a
    /// second pass over a spreadsheet somebody has been editing; failing the whole file because one
    /// row was already imported would make the feature unusable. But a silent skip is worse than
    /// either — "100 imported" when 60 were written is how somebody comes to believe a term exists
    /// that does not. Both numbers go back to the caller.
    ///
    /// The dedupe key is (SourceTerm, TargetTerm), case-insensitive, and it checks the incoming
    /// batch as well as the stored rows: a spreadsheet with the same pair twice must not become two
    /// rows just because neither existed when the request arrived.
    ///
    /// Embedding requests are published AFTER the commit, one per written term, and a failure to
    /// publish does not fail the import — the terms are saved and the indexer is a follower.
    /// </summary>
    public async Task<Result<BulkImportGlossaryTermsResultDto>> BulkImportTermsAsync(
        Guid glossaryId,
        BulkImportGlossaryTermsDto dto,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(glossaryId, cancellationToken);
            if (glossary == null)
                return Result.Failure<BulkImportGlossaryTermsResultDto>(
                    $"Glossary with ID {glossaryId} not found.", "NOT_FOUND");

            var existing = await _unitOfWork.GlossaryTerms.FindAsync(
                t => t.GlossaryId == glossaryId, cancellationToken);

            var seen = new HashSet<string>(
                existing.Select(t => $"{t.SourceTerm}{t.TargetTerm}"),
                StringComparer.OrdinalIgnoreCase);

            var errors = new List<string>();
            var written = new List<GlossaryTerm>();

            foreach (var row in dto.Terms)
            {
                var source = row.SourceTerm?.Trim() ?? string.Empty;
                var target = row.TargetTerm?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                {
                    errors.Add($"'{source}': a term needs both a source and a target.");
                    continue;
                }

                // The unit separator is the delimiter on purpose: a term may legitimately contain a
                // comma, a pipe or a colon, and any of those as a key separator would collide two
                // distinct pairs into one.
                if (!seen.Add($"{source}{target}"))
                {
                    errors.Add($"'{source}' → '{target}': already in this glossary.");
                    continue;
                }

                var term = (row with { SourceTerm = source, TargetTerm = target }).ToEntity(glossaryId);
                await _unitOfWork.GlossaryTerms.AddAsync(term, cancellationToken);
                written.Add(term);
            }

            if (written.Count > 0)
            {
                glossary.TermCount += written.Count;
                glossary.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.Glossaries.Update(glossary);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            foreach (var term in written)
            {
                await TryPublishEmbeddingIndexRequestAsync(glossary.WorkspaceId, term, cancellationToken);
            }

            return Result.Success(new BulkImportGlossaryTermsResultDto(
                Imported: written.Count,
                Skipped: dto.Terms.Count - written.Count,
                Errors: errors));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk importing terms into glossary {GlossaryId}", glossaryId);
            return Result.Failure<BulkImportGlossaryTermsResultDto>(
                "An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<GlossaryTermDto>>> GetTermsByGlossaryIdAsync(Guid glossaryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var terms = await _unitOfWork.GlossaryTerms.FindAsync(t => t.GlossaryId == glossaryId, cancellationToken);
            return Result.Success(terms.Select(t => t.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting terms for glossary {GlossaryId}", glossaryId);
            return Result.Failure<IEnumerable<GlossaryTermDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> UpdateTermAsync(Guid glossaryId, Guid termId, UpdateGlossaryTermDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = await _unitOfWork.GlossaryTerms.GetByIdAsync(termId, cancellationToken);
            if (term == null)
                return Result.Failure($"Term with ID {termId} not found.", "NOT_FOUND");

            if (term.GlossaryId != glossaryId)
                return Result.Failure("Term does not belong to the specified Glossary.", "BAD_REQUEST");

            term.SourceTerm = dto.SourceTerm;
            term.TargetTerm = dto.TargetTerm;
            term.Context = dto.Context;
            term.Domain = dto.Domain;
            term.Definition = dto.Definition;
            term.UsageNote = dto.UsageNote;
            term.PartOfSpeech = dto.PartOfSpeech;
            term.Priority = dto.Priority;
            term.IsActive = dto.IsActive;
            term.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.GlossaryTerms.Update(term);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(glossaryId, cancellationToken);
            if (glossary != null)
            {
                await TryPublishEmbeddingIndexRequestAsync(glossary.WorkspaceId, term, cancellationToken);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating term {TermId}", termId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> DeleteTermAsync(Guid glossaryId, Guid termId, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = await _unitOfWork.GlossaryTerms.GetByIdAsync(termId, cancellationToken);
            if (term == null)
                return Result.Failure($"Term with ID {termId} not found.", "NOT_FOUND");

            if (term.GlossaryId != glossaryId)
                return Result.Failure("Term does not belong to the specified Glossary.", "BAD_REQUEST");

            _unitOfWork.GlossaryTerms.Remove(term);

            var glossary = await _unitOfWork.Glossaries.GetByIdAsync(glossaryId, cancellationToken);
            if (glossary != null)
            {
                glossary.TermCount = Math.Max(0, glossary.TermCount - 1);
                glossary.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.Glossaries.Update(glossary);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Removes the term's vector from workspace_{id} so semantic search stops
            // surfacing it — the hard Remove() above only deletes the Postgres row; without
            // this the vector indexed by TryPublishEmbeddingIndexRequestAsync (on create/
            // update) would otherwise stay in Qdrant forever. Only possible when the
            // glossary itself still exists (need its WorkspaceId for the collection id) —
            // if it's already gone there's no collection to clean up anyway.
            if (glossary != null)
            {
                await TryPublishEmbeddingDeleteRequestAsync(glossary.WorkspaceId, termId, cancellationToken);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting term {TermId}", termId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    /// <summary>
    /// Wires a glossary term into the RAG pipeline by publishing to the "embedding:index_requests"
    /// Redis Stream that warptalk-ai's EmbeddingWorker consumes. Field names must match
    /// EmbeddingIndexRequest.from_redis() in warptalk-ai/embedding_worker/schemas.py exactly;
    /// chunk keys (id/text/metadata) must match EmbeddingChunk. collection_id follows the
    /// "workspace_{id}" convention chat_tools.py's semantic_search already assumes. A publish
    /// failure must not fail the term create/update it rides along with, so it's swallowed here
    /// (logged only) rather than propagated.
    /// </summary>
    private async Task TryPublishEmbeddingIndexRequestAsync(Guid workspaceId, GlossaryTerm term, CancellationToken ct)
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(term.Context)
                ? $"{term.SourceTerm} → {term.TargetTerm}"
                : $"{term.SourceTerm} → {term.TargetTerm}: {term.Context}";

            var chunk = new
            {
                id = term.Id.ToString(),
                text,
                metadata = new
                {
                    glossary_id = term.GlossaryId.ToString(),
                    term_id = term.Id.ToString(),
                    source_term = term.SourceTerm,
                    target_term = term.TargetTerm,
                    domain = term.Domain,
                },
            };

            var entries = new NameValueEntry[]
            {
                new("job_id", Guid.NewGuid().ToString()),
                new("workspace_id", workspaceId.ToString()),
                new("collection_id", $"workspace_{workspaceId}"),
                new("source_type", "glossary_term"),
                new("source_id", term.Id.ToString()),
                new("chunks_json", JsonSerializer.Serialize(new[] { chunk })),
                new("external_llm_allowed", "true"),
                new("ai_retrieval_allowed", term.IsActive ? "true" : "false"),
                new("retention_state", "active"),
                new("deletion_state", term.DeletedAt == null ? "active" : "deleted"),
                new("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            };

            var db = _redis.GetDatabase();
            await db.StreamAddAsync("embedding:index_requests", entries, maxLength: 10000, useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish embedding index request for term {TermId}", term.Id);
        }
    }

    /// <summary>
    /// Removes a term's vector from "workspace_{workspaceId}" via EmbeddingWorker.process's
    /// explicit deletion_state="deleted" path (warptalk-ai/embedding_worker/worker.py). Safe
    /// to call for a term that was never actually indexed — Qdrant delete-by-id and a missing
    /// collection are both treated as a no-op on that side. A publish failure must not fail
    /// the delete it rides along with, so it's swallowed here (logged only).
    /// </summary>
    private async Task TryPublishEmbeddingDeleteRequestAsync(Guid workspaceId, Guid termId, CancellationToken ct)
    {
        try
        {
            var chunk = new { id = termId.ToString(), text = "", metadata = new { } };

            var entries = new NameValueEntry[]
            {
                new("job_id", Guid.NewGuid().ToString()),
                new("workspace_id", workspaceId.ToString()),
                new("collection_id", $"workspace_{workspaceId}"),
                new("source_type", "glossary_term"),
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
            _logger.LogWarning(ex, "Failed to publish embedding delete request for term {TermId}", termId);
        }
    }
}
