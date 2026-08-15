using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Adapters;

public class DocumentEmbeddingResultProcessor : IDocumentEmbeddingResultProcessor
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceDocumentEventPublisher _eventPublisher;
    private readonly ILogger<DocumentEmbeddingResultProcessor> _logger;

    public DocumentEmbeddingResultProcessor(
        IUnitOfWork unitOfWork,
        IWorkspaceDocumentEventPublisher eventPublisher,
        ILogger<DocumentEmbeddingResultProcessor> logger)
    {
        _unitOfWork = unitOfWork;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task ProcessResultAsync(Dictionary<string, string> values, CancellationToken ct = default)
    {
        var sourceId = values.GetValueOrDefault("source_id");
        if (!Guid.TryParse(sourceId, out var documentId))
        {
            _logger.LogWarning("Embedding result missing valid source_id. SourceId: {SourceId}", sourceId);
            return;
        }

        var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
        if (document == null || document.DeletedAt != null)
        {
            _logger.LogWarning("Embedding result document not found or deleted. DocumentId: {DocumentId}", documentId);
            return;
        }

        var status = values.GetValueOrDefault("status") ?? string.Empty;
        var jobId = values.GetValueOrDefault("job_id") ?? string.Empty;
        var provider = values.GetValueOrDefault("provider") ?? string.Empty;
        var model = values.GetValueOrDefault("model") ?? string.Empty;
        var dimensions = values.GetValueOrDefault("dimensions") ?? string.Empty;
        var reason = values.GetValueOrDefault("reason") ?? string.Empty;
        var chunksIndexed = ParseInt(values.GetValueOrDefault("chunks_indexed"));

        if (string.Equals(status, "indexed", StringComparison.OrdinalIgnoreCase))
        {
            document.LastIndexedAt = DateTime.UtcNow;
            document.IndexVersion = BuildIndexVersion(provider, model, dimensions);
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.completed.ToString();
            // Cleared on success so a reason from an earlier attempt cannot outlive the failure
            // it described — the same rule the guardrail applies on its clean pass.
            document.IngestionFailureReason = null;
            document.AiEligible =
                document.IsAiAllowed &&
                !document.IsRestricted() &&
                string.Equals(
                    document.Status,
                    WorkspaceDocumentStatus.@public.ToString(),
                    StringComparison.OrdinalIgnoreCase);
            document.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);
            await PublishLifecycleAsync(document, WorkspaceDocumentConstants.LifecycleEvents.Completed, ct);

            await _unitOfWork.AuditAsync(
                document.Id,
                document.WorkspaceId,
                null,
                WorkspaceDocumentConstants.AuditActions.EmbeddingIndexed,
                new { jobId, provider, model, dimensions, chunksIndexed },
                _logger,
                ct);
        }
        else if (string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase))
        {
            document.AiEligible = false;
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
            document.IngestionFailureReason = WorkspaceDocumentIngestionFailureReasons.EmbeddingBlocked;
            document.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);
            await PublishLifecycleAsync(document, WorkspaceDocumentConstants.LifecycleEvents.Updated, ct);

            await _unitOfWork.AuditAsync(
                document.Id,
                document.WorkspaceId,
                null,
                WorkspaceDocumentConstants.AuditActions.EmbeddingBlocked,
                new { jobId, provider, model, dimensions, reason },
                _logger,
                ct);
        }
        else if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            document.AiEligible = false;
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.failed.ToString();
            // WT-411 gave the guardrail's branches a reason and missed this one. A failure the
            // embedding worker REPORTED and a failure the guardrail never got past produced an
            // identical row — ingestion_status='failed', reason NULL — so the owner of a
            // document could not tell a retryable outage from a policy refusal, and neither
            // could anyone reading the table afterwards.
            document.IngestionFailureReason = WorkspaceDocumentIngestionFailureReasons.EmbeddingFailed;
            document.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);
            await PublishLifecycleAsync(document, WorkspaceDocumentConstants.LifecycleEvents.Failed, ct);

            await _unitOfWork.AuditAsync(
                document.Id,
                document.WorkspaceId,
                null,
                WorkspaceDocumentConstants.AuditActions.EmbeddingFailed,
                new { jobId, provider, model, dimensions, reason },
                _logger,
                ct);
        }
        else
        {
            _logger.LogWarning("Unknown embedding result status. DocumentId: {DocumentId}, Status: {Status}, JobId: {JobId}", document.Id, status, jobId);
        }
    }

    private Task PublishLifecycleAsync(
        WorkspaceDocument document,
        string eventType,
        CancellationToken ct)
    {
        return _eventPublisher.PublishDocumentLifecycleAsync(
            document.Id,
            document.WorkspaceId,
            document.Status,
            document.IngestionStatus,
            eventType,
            document.UpdatedAt,
            document.UploadedBy,
            ct);
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static string BuildIndexVersion(string provider, string model, string dimensions)
    {
        var version = $"{provider}/{model}/{dimensions}".Trim('/');
        return version.Length <= 50 ? version : version[..50];
    }
}
