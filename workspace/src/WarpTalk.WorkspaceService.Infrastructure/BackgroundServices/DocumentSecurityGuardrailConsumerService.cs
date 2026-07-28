using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;

namespace WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

/// <summary>
/// Background consumer service performing Pre-Ingestion Security Guardrails (PII/DLP scans)
/// and direct AI Ingestion (chunking, OpenAI embeddings, and Qdrant vector sync).
/// </summary>
public class DocumentSecurityGuardrailConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<DocumentSecurityGuardrailConsumerService> _logger;
    private readonly IServiceProvider _serviceProvider;

    private const string StreamKey = "workspace-document-events";
    private const string ConsumerGroup = "workspace-document-ingestion";
    private readonly string _consumerName = $"workspace-ingestion-{Environment.MachineName}-{Guid.NewGuid():N}";

    public DocumentSecurityGuardrailConsumerService(
        IConnectionMultiplexer redis,
        ILogger<DocumentSecurityGuardrailConsumerService> logger,
        IServiceProvider serviceProvider)
    {
        _redis = redis;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DocumentSecurityGuardrailConsumerService started.");
        var db = _redis.GetDatabase();

        // Step 1: Ensure Redis Stream Consumer Group exists
        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroup, "0-0", true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Redis Stream Consumer Group for stream: {StreamKey}", StreamKey);
        }

        // Step 2: Enter stream consumption loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await db.StreamReadGroupAsync(StreamKey, ConsumerGroup, _consumerName, count: 5);
                if (messages.Length == 0)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                foreach (var message in messages)
                {
                    var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
                    var eventType = values.GetValueOrDefault("event_type");

                    if (string.Equals(eventType, "DocumentUploaded", StringComparison.OrdinalIgnoreCase))
                    {
                        var documentIdStr = values.GetValueOrDefault("document_id");
                        if (Guid.TryParse(documentIdStr, out var documentId))
                        {
                            var handled = await ProcessDocumentUploadAsync(documentId, values, stoppingToken);
                            if (!handled)
                            {
                                continue;
                            }
                        }
                    }

                    // Acknowledge stream message
                    await db.StreamAcknowledgeAsync(StreamKey, ConsumerGroup, message.Id);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in DocumentSecurityGuardrailConsumerService processing loop.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    public async Task<bool> ProcessDocumentUploadAsync(Guid documentId, Dictionary<string, string> eventValues, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var storage = scope.ServiceProvider.GetRequiredService<IWorkspaceDocumentStorage>();
        var textExtractor = scope.ServiceProvider.GetRequiredService<IDocumentTextExtractor>();
        var securityScanner = scope.ServiceProvider.GetRequiredService<IDocumentSecurityScanner>();
        var policyResolver = scope.ServiceProvider.GetRequiredService<IAiPolicyResolver>();
        var embeddingPublisher = scope.ServiceProvider.GetRequiredService<IEmbeddingIndexPublisher>();
        var lifecyclePublisher = scope.ServiceProvider.GetRequiredService<IWorkspaceDocumentEventPublisher>();

        WorkspaceDocument? document = null;
        try
        {
            document = await unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.DeletedAt != null)
            {
                _logger.LogWarning("Document {DocumentId} not found or soft-deleted. Skipping guardrails & ingestion.", documentId);
                return true;
            }

            if (!HasBasicIndexEligibility(document))
            {
                await MarkSkippedAsync(document, unitOfWork, lifecyclePublisher, ct);
                return true;
            }

            document.IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString();
            document.UpdatedAt = DateTime.UtcNow;
            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);
            await lifecyclePublisher.PublishDocumentLifecycleAsync(
                document.Id,
                document.WorkspaceId,
                document.Status,
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Processing,
                document.UpdatedAt,
                document.UploadedBy,
                ct);

            var policy = await policyResolver.ResolvePolicySettingsAsync(unitOfWork, document, ct);

            ExtractedDocumentContent content;
            using (var decryptedStream = await storage.GetDecryptedStreamAsync(document, ct))
            {
                content = await textExtractor.ExtractTextAsync(decryptedStream, document.FileExtension, ct);
            }

            var jsonContent = JsonSerializer.Serialize(content);
            await storage.SaveExtractedTextAsync(document, jsonContent, ct);

            var scanResult = await securityScanner.ScanAsync(
                content.FullText,
                policy.PiiEnabled,
                policy.DlpEnabled,
                policy.KeywordsBlacklist,
                ct);

            if (scanResult.PiiDetected)
            {
                _logger.LogInformation("PII violation detected in document {DocumentId}", documentId);
            }
            if (scanResult.DlpDetected)
            {
                _logger.LogInformation("DLP keyword violation detected in document {DocumentId}", documentId);
            }

            if (scanResult.ViolationFound)
            {
                document.ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel;
            }

            var isApproved = string.Equals(
                document.Status,
                WorkspaceDocumentStatus.@public.ToString(),
                StringComparison.OrdinalIgnoreCase);
            var canIndex = document.IsAiAllowed
                && !document.IsRestricted()
                && isApproved
                && string.Equals(document.RetentionState, "active", StringComparison.OrdinalIgnoreCase)
                && !scanResult.ViolationFound;

            document.AiEligible = false;
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString();
            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);

            if (canIndex)
            {
                var textToIngest = string.IsNullOrWhiteSpace(scanResult.MaskedContent)
                    ? content.FullText
                    : scanResult.MaskedContent;
                try
                {
                    var jobId = await embeddingPublisher.PublishEmbeddingIndexRequestAsync(
                        document,
                        textToIngest,
                        policy.AllowExternalLlm,
                        ct);
                    if (jobId is null)
                    {
                        document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish embedding request for document {DocumentId}.", documentId);
                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.failed.ToString();
                }
            }
            else
            {
                document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
            }

            document.UpdatedAt = DateTime.UtcNow;
            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing AI Ingestion/Guardrails for document {DocumentId}. Applying fail-safe security fallback.", documentId);

            // Fail-Safe Fallback (Fail-Closed Policy): Default to restricted access on error
            if (document != null)
            {
                try
                {
                    document.ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel;
                    document.AiEligible = false;
                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.failed.ToString();

                    unitOfWork.WorkspaceDocumentRepository.Update(document);
                    await unitOfWork.SaveChangesAsync(ct);
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Failed to apply fail-safe DB fallback for document {DocumentId}", documentId);
                    return false;
                }
            }

            return document is not null;
        }
    }

    private static bool HasBasicIndexEligibility(WorkspaceDocument document)
    {
        return document.IsAiAllowed
            && string.Equals(
                document.Status,
                WorkspaceDocumentStatus.@public.ToString(),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(document.RetentionState, "active", StringComparison.OrdinalIgnoreCase)
            && !document.IsRestricted();
    }

    private static async Task MarkSkippedAsync(
        WorkspaceDocument document,
        IUnitOfWork unitOfWork,
        IWorkspaceDocumentEventPublisher lifecyclePublisher,
        CancellationToken ct)
    {
        document.AiEligible = false;
        document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
        document.UpdatedAt = DateTime.UtcNow;
        unitOfWork.WorkspaceDocumentRepository.Update(document);
        await unitOfWork.SaveChangesAsync(ct);
        await lifecyclePublisher.PublishDocumentLifecycleAsync(
            document.Id,
            document.WorkspaceId,
            document.Status,
            document.IngestionStatus,
            WorkspaceDocumentConstants.LifecycleEvents.Updated,
            document.UpdatedAt,
            document.UploadedBy,
            ct);
    }

    private async Task<(bool PiiEnabled, bool DlpEnabled, List<string>? Keywords, bool AllowExternalLlm)> ResolvePolicySettingsAsync(IUnitOfWork unitOfWork, WorkspaceDocument document, CancellationToken ct)
    {
        bool piiEnabled = false;
        bool dlpEnabled = false;
        List<string>? keywordsBlacklist = null;
        // Opt-out semantics (nullable bool): unset at both document and workspace level ⇒ allowed.
        bool allowExternalLlm = true;

        // A. Parse Document-level AI Usage Policy if present
        AiUsagePolicyConfiguration? docPolicy = null;
        if (!string.IsNullOrWhiteSpace(document.AiUsagePolicy))
        {
            try
            {
                docPolicy = JsonSerializer.Deserialize<AiUsagePolicyConfiguration>(document.AiUsagePolicy, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize AiUsagePolicy for document {DocumentId}", document.Id);
            }
        }

        // B. Parse Workspace-level default configurations
        WorkspaceConfiguration? wsConfig = null;
        var workspace = await unitOfWork.WorkspaceRepository.GetByIdAsync(document.WorkspaceId, ct);
        if (workspace != null)
        {
            wsConfig = WorkspaceHelper.GetWorkspaceConfig(workspace);
        }

        // C. Apply Hierarchy & Fallbacks
        if (docPolicy?.RedactPii != null)
        {
            piiEnabled = docPolicy.RedactPii.Enabled;
        }
        else if (wsConfig?.AiUsagePolicy?.RedactPii != null)
        {
            piiEnabled = wsConfig.AiUsagePolicy.RedactPii.Enabled;
        }

        if (docPolicy?.Dlp != null)
        {
            dlpEnabled = docPolicy.Dlp.Enabled;
            keywordsBlacklist = docPolicy.Dlp.KeywordsBlacklist;
        }
        else if (wsConfig?.AiUsagePolicy?.Dlp != null)
        {
            dlpEnabled = wsConfig.AiUsagePolicy.Dlp.Enabled;
            keywordsBlacklist = wsConfig.AiUsagePolicy.Dlp.KeywordsBlacklist;
        }

        if (docPolicy?.AllowExternalLlm.HasValue == true)
        {
            allowExternalLlm = docPolicy.AllowExternalLlm.Value;
        }
        else if (wsConfig?.AiUsagePolicy?.AllowExternalLlm.HasValue == true)
        {
            allowExternalLlm = wsConfig.AiUsagePolicy.AllowExternalLlm.Value;
        }

        return (piiEnabled, dlpEnabled, keywordsBlacklist, allowExternalLlm);
    }

    private const int EmbeddingChunkCharLimit = 2000;

    /// <summary>
    /// Wires an approved, AI-eligible document's extracted text into the RAG pipeline by
    /// publishing to the "embedding:index_requests" Redis Stream that warptalk-ai's
    /// EmbeddingWorker consumes. Field names must match EmbeddingIndexRequest.from_redis() in
    /// warptalk-ai/embedding_worker/schemas.py exactly; chunk keys (id/text/metadata) must match
    /// EmbeddingChunk. collection_id follows the "workspace_{id}" convention chat_tools.py's
    /// semantic_search already assumes.
    /// </summary>
    private async Task PublishEmbeddingIndexRequestAsync(
        WorkspaceDocument document, string fullText, bool externalLlmAllowed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fullText)) return;

        var chunks = ChunkText(fullText, EmbeddingChunkCharLimit)
            .Select((text, index) => new
            {
                id = $"{document.Id}_{index}",
                text,
                metadata = new
                {
                    document_id = document.Id.ToString(),
                    document_name = document.Name,
                    chunk_index = index,
                },
            })
            .ToList();
        if (chunks.Count == 0) return;

        var entries = new NameValueEntry[]
        {
            new("job_id", Guid.NewGuid().ToString()),
            new("workspace_id", document.WorkspaceId.ToString()),
            new("collection_id", $"workspace_{document.WorkspaceId}"),
            new("source_type", "document"),
            new("source_id", document.Id.ToString()),
            new("chunks_json", JsonSerializer.Serialize(chunks)),
            new("external_llm_allowed", externalLlmAllowed ? "true" : "false"),
            new("ai_retrieval_allowed", document.AiEligible ? "true" : "false"),
            new("retention_state", document.RetentionState),
            new("deletion_state", document.DeletedAt == null ? "active" : "deleted"),
            new("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
        };

        var db = _redis.GetDatabase();
        await db.StreamAddAsync("embedding:index_requests", entries, maxLength: 10000, useApproximateMaxLength: true);
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i)).Trim();
            if (chunk.Length > 0) yield return chunk;
        }
    }
}
