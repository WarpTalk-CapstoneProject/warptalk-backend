using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;

namespace WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

public class DocumentAiIngestionConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<DocumentAiIngestionConsumerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private const string StreamKey = "workspace-document-events";
    private const string ConsumerGroup = "workspace-document-ingestion";
    private readonly string _consumerName = $"workspace-ingestion-{Environment.MachineName}-{Guid.NewGuid():N}";

    // Common PII Regular Expressions
    private static readonly Regex EmailRegex = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PhoneRegex = new(@"\b(?:\+?84|0)\d{9,10}\b", RegexOptions.Compiled);

    public DocumentAiIngestionConsumerService(
        IConnectionMultiplexer redis,
        ILogger<DocumentAiIngestionConsumerService> logger,
        IServiceProvider serviceProvider)
    {
        _redis = redis;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DocumentAiIngestionConsumerService started.");
        var db = _redis.GetDatabase();

        // Ensure stream and consumer group exists
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
                _logger.LogError(ex, "Error occurred in DocumentAiIngestionConsumerService processing loop.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    public async Task ProcessDocumentUploadAsync(Guid documentId, Dictionary<string, string> eventValues, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        WorkspaceDocument? document = null;
        try
        {
            document = await unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.DeletedAt != null)
            {
                _logger.LogWarning("Document {DocumentId} not found or soft-deleted. Skipping ingestion.", documentId);
                return;
            }

            // Set ingestion status to processing
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.processing.ToString();
            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);

            // 1. Resolve Effective AI Usage Policy (Inheritance & Fallback)
            var (piiEnabled, dlpEnabled, keywordsBlacklist) = await ResolvePolicySettingsAsync(unitOfWork, document, ct);

            // 2. Read Document Content (Simulated storage read)
            var content = MockReadDocumentContent(document);

            // 3. Scan for Guardrail Violations
            bool violationFound = false;

            // PII Scan
            if (piiEnabled)
            {
                if (EmailRegex.IsMatch(content) || PhoneRegex.IsMatch(content))
                {
                    _logger.LogInformation("PII detected in document {DocumentId}", documentId);
                    violationFound = true;
                }
            }

            // DLP Scan (Case-insensitive substring match)
            if (dlpEnabled && keywordsBlacklist != null)
            {
                foreach (var keyword in keywordsBlacklist)
                {
                    if (!string.IsNullOrWhiteSpace(keyword) && content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("DLP keyword violation ('{Keyword}') detected in document {DocumentId}", keyword, documentId);
                        violationFound = true;
                        break;
                    }
                }
            }

            // 4. Update Document State based on Scan results
            // Preserve manual upload sensitivity if it was already true
            document.IsSensitive = document.IsSensitive || violationFound;
            document.ConfidentialityLevel = WorkspaceDocumentHelper.GetConfidentialityLevel(document.IsSensitive);
            document.AiEligible = !document.IsSensitive; // Not eligible for AI retrieval if sensitive
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.completed.ToString();

            unitOfWork.WorkspaceDocumentRepository.Update(document);
            await unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Successfully finalized ingestion scan for document {DocumentId}. IsSensitive: {IsSensitive}, AiEligible: {AiEligible}", 
                documentId, document.IsSensitive, document.AiEligible);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing AI Ingestion for document {DocumentId}. Applying fail-safe security fallback.", documentId);

            // Fail-Safe Fallback: If scanner fails, default to restricted access
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

    private string MockReadDocumentContent(WorkspaceDocument document)
    {
        // Mock reading document content using specific keywords in FileName for testing purposes
        if (document.FileName.Contains("sensitive_test_pii", StringComparison.OrdinalIgnoreCase))
        {
            return "This document belongs to John Doe. Contact email: john.doe@example.com. Phone: 0987654321.";
        }
        
        if (document.FileName.Contains("sensitive_test_dlp", StringComparison.OrdinalIgnoreCase))
        {
            return "Báo cáo nội bộ về doanh thu và kế hoạch tăng trưởng doanh số quý tiếp theo.";
        }

        return "This is a clean, non-sensitive document for workspace collaboration.";
    }
}
