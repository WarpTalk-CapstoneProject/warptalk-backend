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
        var policyResolver = scope.ServiceProvider.GetRequiredService<IAiPolicyResolver>();
        var embeddingPublisher = scope.ServiceProvider.GetRequiredService<IEmbeddingIndexPublisher>();

        WorkspaceDocument? document = null;
        try
        {
            document = await unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.DeletedAt != null)
            {
                _logger.LogWarning("Document {DocumentId} not found or soft-deleted. Skipping guardrails & ingestion.", documentId);
                return;
            }

            // Set ingestion status to processing
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString();
            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);

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

            if (scanResult.ViolationFound)
            {
                document.ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel;
            }

            var isApproved = string.Equals(document.Status, WorkspaceDocumentStatus.@public.ToString(), StringComparison.OrdinalIgnoreCase);
            document.AiEligible = document.IsAiAllowed && !document.IsRestricted() && isApproved;

            document.IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString();
            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);

            // 4. Wire into the RAG pipeline via IEmbeddingIndexPublisher
            if (document.AiEligible)
            {
                try
                {
                    var embeddingJobId = await embeddingPublisher.PublishEmbeddingIndexRequestAsync(
                        document,
                        content.FullText,
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
                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.failed.ToString();
                }
            }
            else
            {
                document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
            }

            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Completed security guardrails for document {DocumentId}. ConfidentialityLevel: {ConfidentialityLevel}, AiEligible: {AiEligible}, IngestionStatus: {IngestionStatus}",
                documentId, document.ConfidentialityLevel, document.AiEligible, document.IngestionStatus);
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
                }
            }
        }
    }
}
