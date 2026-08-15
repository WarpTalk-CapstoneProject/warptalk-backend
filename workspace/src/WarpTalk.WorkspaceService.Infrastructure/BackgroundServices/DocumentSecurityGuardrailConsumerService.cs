using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Helpers;

namespace WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

/// <summary>
/// Background consumer service performing Pre-Ingestion Security Guardrails (PII/DLP scans)
/// and coordinating AI Ingestion RAG pipeline delivery.
/// Delegated to IAiPolicyResolver and IEmbeddingIndexPublisher for clean architecture SRP.
/// </summary>
public class DocumentSecurityGuardrailConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<DocumentSecurityGuardrailConsumerService> _logger;
    private readonly IServiceProvider _serviceProvider;

    private const string StreamKey = "workspace-document-events";
    private const string ConsumerGroup = "workspace-document-ingestion";

    /// <summary>
    /// Entries deliberately left unacknowledged by <see cref="ProcessDocumentUploadAsync"/> — it
    /// returns false when even the fail-safe write did not land — used to sit here forever,
    /// because nothing reclaimed them and <c>"&gt;"</c> never returns them again. An upload whose
    /// scan never completed stayed invisible instead of being retried.
    /// </summary>
    private const long ReclaimIdleMilliseconds = 60_000;

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

        // Step 1: Ensure Redis Stream Consumer Group exists.
        // This catch-all already stopped StopHost, but it swallowed once and never retried: after
        // a Redis outage at startup the group was never created, so every StreamReadGroupAsync
        // below failed NOGROUP forever and the DLP/PII guardrail ran deaf while looking alive.
        // Retry with bounded backoff instead, so it recovers on its own once Redis returns.
        if (!await EnsureConsumerGroupAsync(db, stoppingToken))
            return;

        // Step 2: Enter stream consumption loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Reclaim before reading new work. Leaving an entry pending is this consumer's
                // deliberate retry signal, and without a reclaim that signal had no receiver.
                var reclaimed = await db.StreamAutoClaimAsync(
                    StreamKey,
                    ConsumerGroup,
                    _consumerName,
                    ReclaimIdleMilliseconds,
                    "0-0",
                    count: 5);
                var messages = reclaimed.ClaimedEntries;

                if (messages.Length == 0)
                {
                    messages = await db.StreamReadGroupAsync(
                        StreamKey, ConsumerGroup, _consumerName, position: ">", count: 5);
                }

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
                                _logger.LogWarning(
                                    "Document event {MessageId} remains pending because processing and fail-safe persistence did not complete. "
                                    + "It will be reclaimed and retried after {ReclaimIdleMilliseconds}ms.",
                                    message.Id,
                                    ReclaimIdleMilliseconds);
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

    /// <returns>true once the group exists; false only when the host is shutting down.</returns>
    private async Task<bool> EnsureConsumerGroupAsync(IDatabase db, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await db.StreamCreateConsumerGroupAsync(StreamKey, ConsumerGroup, "0-0", true);
                return true;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                // Group already exists
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogError(
                    ex,
                    "Failed to initialize Redis Stream Consumer Group {Group} for stream {StreamKey} "
                    + "(attempt {Attempt}); retrying in {RetryDelay}. Uploaded documents are NOT being "
                    + "PII/DLP scanned or indexed until it succeeds.",
                    ConsumerGroup, StreamKey, attempt, retryDelay);

                try
                {
                    await Task.Delay(retryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        return false;
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

            // These conditions are definitive before content processing. Do not
            // decrypt, extract, scan, or publish more lifecycle traffic for a
            // document that can never enter AI ingestion. AiEligible is not used
            // here because false is also the valid initial state of an approved
            // document waiting for security/indexing to complete.
            if (!DocumentSecurityGuardrailHelper.HasBasicIndexEligibility(document))
            {
                await DocumentSecurityGuardrailHelper.MarkSkippedAsync(document, unitOfWork, lifecyclePublisher, ct);
                _logger.LogInformation(
                    "Skipped document before content processing. DocumentId: {DocumentId}, Status: {Status}, IsAiAllowed: {IsAiAllowed}, RetentionState: {RetentionState}",
                    document.Id,
                    document.Status,
                    document.IsAiAllowed,
                    document.RetentionState);
                return true;
            }

            // Set ingestion status to processing
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

            // 1. Resolve Effective AI Usage Policy via IAiPolicyResolver
            var policy = await policyResolver.ResolvePolicySettingsAsync(unitOfWork, document, ct);

            // 2. Read Document Content (Physical storage read + decryption)
            ExtractedDocumentContent content;
            using (var decryptedStream = await storage.GetDecryptedStreamAsync(document, ct))
            {
                content = await textExtractor.ExtractTextAsync(decryptedStream, document.FileExtension, ct);
            }

            // 2.5 Save the extracted structured content serialized as JSON on disk
            var jsonContent = JsonSerializer.Serialize(content);
            await storage.SaveExtractedTextAsync(document, jsonContent, ct);

            // 3. Scan for Guardrail Violations
            var scanResult = await securityScanner.ScanAsync(
                content.FullText,
                policy.PiiEnabled,
                policy.DlpEnabled,
                policy.KeywordsBlacklist,
                ct);

            await unitOfWork.AuditAsync(
                document.Id,
                document.WorkspaceId,
                null,
                WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted,
                new
                {
                    scanResult.ViolationFound,
                    scanResult.PiiDetected,
                    scanResult.DlpDetected,
                    policy.PiiEnabled,
                    policy.DlpEnabled
                },
                _logger,
                ct);

            if (scanResult.PiiDetected)
            {
                _logger.LogInformation("PII violation detected in document {DocumentId}", documentId);
            }
            if (scanResult.DlpDetected)
            {
                _logger.LogInformation("DLP keyword violation detected in document {DocumentId}", documentId);
            }

            var wasRestrictedBeforeScan = document.IsRestricted();

            if (scanResult.PiiDetected || scanResult.DlpDetected)
            {
                document.ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel;
            }

            var isApproved = string.Equals(document.Status, WorkspaceDocumentStatus.@public.ToString(), StringComparison.OrdinalIgnoreCase);
            var hasMaskedContent = !string.IsNullOrWhiteSpace(scanResult.MaskedContent);
            var canIndex = document.IsAiAllowed
                && !wasRestrictedBeforeScan
                && isApproved
                && string.Equals(document.RetentionState, "active", StringComparison.OrdinalIgnoreCase)
                && !scanResult.DlpDetected
                && (!scanResult.PiiDetected || hasMaskedContent);

            if (scanResult.PiiDetected && !hasMaskedContent)
            {
                _logger.LogWarning(
                    "Skipping embedding for document {DocumentId} because PII was detected but masked content was unavailable.",
                    documentId);
            }

            // WT-411: a refusal the scan actually reached is a different fact from a scan that
            // never answered, and the fail-safe below records the other one.
            if (scanResult.DlpDetected)
            {
                document.IngestionFailureReason = WorkspaceDocumentIngestionFailureReasons.DlpDetected;
            }
            else if (scanResult.PiiDetected && !hasMaskedContent)
            {
                document.IngestionFailureReason = WorkspaceDocumentIngestionFailureReasons.PiiUnmasked;
            }
            else
            {
                // Cleared on every clean pass, so a stale reason from an earlier attempt cannot
                // outlive the failure it described.
                document.IngestionFailureReason = null;
            }

            var textToIngest = scanResult.PiiDetected
                ? scanResult.MaskedContent!
                : content.FullText;

            // AiEligible means retrieval is ready, not merely that indexing may
            // start. It is enabled only by DocumentEmbeddingResultProcessor after
            // the embedding worker reports a successful Qdrant upsert.
            document.AiEligible = false;

            document.IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString();
            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);

            // 4. Wire into the RAG pipeline via IEmbeddingIndexPublisher using Masked Text
            if (canIndex)
            {
                try
                {
                    var embeddingJobId = await embeddingPublisher.PublishEmbeddingIndexRequestAsync(
                        document,
                        textToIngest,
                        policy.AllowExternalLlm,
                        ct);

                    if (embeddingJobId == null)
                    {
                        document.AiEligible = false;
                        document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish embedding index request for document {DocumentId}", documentId);
                    document.AiEligible = false;
                    document.IngestionFailureReason =
                        WorkspaceDocumentIngestionFailureReasons.EmbeddingPublishFailed;
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
            await lifecyclePublisher.PublishDocumentLifecycleAsync(
                document.Id,
                document.WorkspaceId,
                document.Status,
                document.IngestionStatus,
                string.Equals(document.IngestionStatus, WorkspaceDocumentIngestionStatus.failed.ToString(), StringComparison.OrdinalIgnoreCase)
                    ? WorkspaceDocumentConstants.LifecycleEvents.Failed
                    : WorkspaceDocumentConstants.LifecycleEvents.Updated,
                document.UpdatedAt,
                document.UploadedBy,
                ct);

            _logger.LogInformation("Completed security guardrails for document {DocumentId}. ConfidentialityLevel: {ConfidentialityLevel}, AiEligible: {AiEligible}, IngestionStatus: {IngestionStatus}",
                documentId, document.ConfidentialityLevel, document.AiEligible, document.IngestionStatus);
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
                    // WT-411: still fail closed — we genuinely do not know what is in this file —
                    // but record that we FAILED TO LOOK rather than that we FOUND something. The
                    // two produced an identical row before, so a document hidden by a timeout was
                    // indistinguishable from one hidden because it contains PII, and neither the
                    // owner nor a retry could tell which.
                    //
                    // The three causes are separated because they point at different components,
                    // and telling them apart is exactly what was missing when five production
                    // documents failed with nothing on record: the audit trail showed the
                    // guardrail reading each file and then no SecurityScanCompleted row at all,
                    // which proves ScanAsync threw but not WHICH way. A timeout blames the
                    // security worker or the queue; scan_failed blames that worker's own upstream;
                    // anything else is ours, here on the ingestion path.
                    document.IngestionFailureReason = ex switch
                    {
                        TimeoutException => WorkspaceDocumentIngestionFailureReasons.SecurityScanTimeout,
                        InvalidOperationException => WorkspaceDocumentIngestionFailureReasons.SecurityScanFailed,
                        _ => WorkspaceDocumentIngestionFailureReasons.IngestionError,
                    };
                    document.UpdatedAt = DateTime.UtcNow;

                    unitOfWork.WorkspaceDocumentRepository.Update(document);
                    await unitOfWork.SaveChangesAsync(ct);
                    await lifecyclePublisher.PublishDocumentLifecycleAsync(
                        document.Id,
                        document.WorkspaceId,
                        document.Status,
                        document.IngestionStatus,
                        WorkspaceDocumentConstants.LifecycleEvents.Failed,
                        document.UpdatedAt,
                        document.UploadedBy,
                        ct);
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Failed to apply fail-safe DB fallback for document {DocumentId}", documentId);
                    return false;
                }

                return true;
            }

            return false;
        }
    }

}
