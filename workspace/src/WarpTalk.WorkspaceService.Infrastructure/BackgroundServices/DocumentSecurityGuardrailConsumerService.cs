using System;
using System.Collections.Generic;
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

        // Launch Phase 3 AI Result Consumer Loop in background
        _ = ConsumeEmbeddingIndexResultsLoopAsync(stoppingToken);

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
                            await ProcessDocumentUploadAsync(documentId, values, stoppingToken);
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

    public async Task ProcessDocumentUploadAsync(Guid documentId, Dictionary<string, string> eventValues, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var storage = scope.ServiceProvider.GetRequiredService<IWorkspaceDocumentStorage>();
        var textExtractor = scope.ServiceProvider.GetRequiredService<IDocumentTextExtractor>();
        var securityScanner = scope.ServiceProvider.GetRequiredService<IDocumentSecurityScanner>();
        var eventPublisher = scope.ServiceProvider.GetRequiredService<IWorkspaceDocumentEventPublisher>();

        WorkspaceDocument? document = null;
        try
        {
            document = await unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.DeletedAt != null)
            {
                _logger.LogWarning("Document {DocumentId} not found or soft-deleted. Skipping guardrails & ingestion.", documentId);
                return;
            }

            if (!document.IsAiAllowed)
            {
                _logger.LogInformation("Document {DocumentId} is marked as Administrative Document (IsAiAllowed = false). Skipping AI Ingestion.", documentId);
                document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
                document.AiEligible = false;
                unitOfWork.WorkspaceDocumentRepository.Update(document);
                await unitOfWork.SaveChangesAsync(ct);
                return;
            }

            // Set ingestion status to processing
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString();
            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);

            // 1. Resolve Effective AI Usage Policy (Inheritance & Fallback)
            var (piiEnabled, dlpEnabled, keywordsBlacklist) = await ResolvePolicySettingsAsync(unitOfWork, document, ct);

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
            var scanResult = await securityScanner.ScanAsync(content.FullText, piiEnabled, dlpEnabled, keywordsBlacklist, ct);
            if (scanResult.PiiDetected)
            {
                _logger.LogInformation("PII violation detected in document {DocumentId}", documentId);
            }
            if (scanResult.DlpDetected)
            {
                _logger.LogInformation("DLP keyword violation detected in document {DocumentId}", documentId);
            }

            bool violationFound = scanResult.ViolationFound;

            document.IsSensitive = document.IsSensitive || violationFound;
            document.ConfidentialityLevel = WorkspaceDocumentHelper.GetConfidentialityLevel(document.IsSensitive);

            if (violationFound)
            {
                document.AiEligible = false;
                document.IngestionStatus = WorkspaceDocumentIngestionStatus.failed.ToString();
                unitOfWork.WorkspaceDocumentRepository.Update(document);
                await unitOfWork.SaveChangesAsync(ct);

                _logger.LogInformation("Security guardrail violation found in document {DocumentId}. Mark failed and non-eligible.", documentId);
            }
            else
            {
                // Clean document: Publish EmbeddingIndexRequest to Redis Stream embedding:index_requests
                document.IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString();
                unitOfWork.WorkspaceDocumentRepository.Update(document);
                await unitOfWork.SaveChangesAsync(ct);

                await eventPublisher.PublishEmbeddingIndexRequestAsync(
                    document.Id,
                    document.WorkspaceId,
                    content.FullText,
                    piiEnabled,
                    ct);

                _logger.LogInformation("Security guardrails clean for document {DocumentId}. Published EmbeddingIndexRequest to AI worker stream.", documentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing AI Ingestion/Guardrails for document {DocumentId}. Applying fail-safe security fallback.", documentId);

            // Fail-Safe Fallback (Fail-Closed Policy): Default to restricted access on error
            if (document != null)
            {
                try
                {
                    document.IsSensitive = true;
                    document.ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel;
                    document.AiEligible = false;
                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.failed.ToString();

                    unitOfWork.WorkspaceDocumentRepository.Update(document);
                    await unitOfWork.SaveChangesAsync(ct);
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Failed to apply fail-safe DB fallback for document {DocumentId}", documentId);
                }
            }
        }
    }

    private async Task<(bool PiiEnabled, bool DlpEnabled, List<string>? Keywords)> ResolvePolicySettingsAsync(IUnitOfWork unitOfWork, WorkspaceDocument document, CancellationToken ct)
    {
        bool piiEnabled = false;
        bool dlpEnabled = false;
        List<string>? keywordsBlacklist = null;

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

        return (piiEnabled, dlpEnabled, keywordsBlacklist);
    }

    private async Task ConsumeEmbeddingIndexResultsLoopAsync(CancellationToken ct)
    {
        const string resultStreamKey = "embedding:index_results";
        const string resultGroup = "workspace-ingestion-results";

        var db = _redis.GetDatabase();

        try
        {
            await db.StreamCreateConsumerGroupAsync(resultStreamKey, resultGroup, "0-0", true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize Consumer Group for stream: {StreamKey}", resultStreamKey);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var messages = await db.StreamReadGroupAsync(resultStreamKey, resultGroup, _consumerName, count: 5);
                if (messages.Length == 0)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                foreach (var message in messages)
                {
                    var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
                    var sourceType = values.GetValueOrDefault("source_type");
                    var sourceIdStr = values.GetValueOrDefault("source_id");
                    var status = values.GetValueOrDefault("status");

                    if (string.Equals(sourceType, "workspace_document", StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParse(sourceIdStr, out var documentId))
                    {
                        using (var scope = _serviceProvider.CreateScope())
                        {
                            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                            var document = await unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
                            if (document != null && document.DeletedAt == null)
                            {
                                if (string.Equals(status, "indexed", StringComparison.OrdinalIgnoreCase))
                                {
                                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.completed.ToString();
                                    document.AiEligible = true;
                                }
                                else
                                {
                                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.failed.ToString();
                                    document.AiEligible = false;
                                }

                                unitOfWork.WorkspaceDocumentRepository.Update(document);
                                await unitOfWork.SaveChangesAsync(ct);

                                _logger.LogInformation("Phase 3 Result Processed for document {DocumentId}. Status: {Status}, AiEligible: {AiEligible}",
                                    documentId, document.IngestionStatus, document.AiEligible);
                            }
                        }
                    }

                    await db.StreamAcknowledgeAsync(resultStreamKey, resultGroup, message.Id);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ConsumeEmbeddingIndexResultsLoopAsync.");
                await Task.Delay(5000, ct);
            }
        }
    }
}
