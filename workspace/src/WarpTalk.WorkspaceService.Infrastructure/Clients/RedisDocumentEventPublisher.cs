using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public class RedisDocumentEventPublisher : IWorkspaceDocumentEventPublisher
{
    private const int StreamMaxLength = 10000;
    private const string RealtimeChannel = "warptalk:documents:events";
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDocumentEventPublisher> _logger;

    public RedisDocumentEventPublisher(IConnectionMultiplexer redis, ILogger<RedisDocumentEventPublisher> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task PublishDocumentUploadedAsync(
        Guid documentId,
        Guid workspaceId,
        string storageKey,
        string fileName,
        string fileExtension,
        Guid userId,
        string? confidentialityLevel = null,
        CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-document-events", new NameValueEntry[]
            {
                new NameValueEntry("event_type", "DocumentUploaded"),
                new NameValueEntry("document_id", documentId.ToString()),
                new NameValueEntry("workspace_id", workspaceId.ToString()),
                new NameValueEntry("storage_key", storageKey),
                new NameValueEntry("file_name", fileName),
                new NameValueEntry("file_extension", fileExtension),
                new NameValueEntry("uploaded_by", userId.ToString()),
                new NameValueEntry("confidentiality_level", confidentialityLevel ?? "general")
            }, maxLength: StreamMaxLength, useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish document upload event to Redis Stream. DocumentId: {DocumentId}", documentId);
        }
    }

    public async Task PublishDocumentDeletedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-document-events", new NameValueEntry[]
            {
                new NameValueEntry("event_type", "DocumentDeleted"),
                new NameValueEntry("document_id", documentId.ToString()),
                new NameValueEntry("workspace_id", workspaceId.ToString())
            }, maxLength: StreamMaxLength, useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish document delete event to Redis Stream. DocumentId: {DocumentId}", documentId);
        }
    }

    public async Task PublishDocumentArchivedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-document-events", new NameValueEntry[]
            {
                new NameValueEntry("event_type", "DocumentArchived"),
                new NameValueEntry("document_id", documentId.ToString()),
                new NameValueEntry("workspace_id", workspaceId.ToString())
            }, maxLength: StreamMaxLength, useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish document archive event to Redis Stream. DocumentId: {DocumentId}", documentId);
        }
    }

    public async Task PublishDocumentLifecycleAsync(
        Guid documentId,
        Guid workspaceId,
        string status,
        string ingestionStatus,
        string eventType,
        DateTime updatedAt,
        Guid? userId = null,
        CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                eventType,
                workspaceId = workspaceId.ToString(),
                documentId = documentId.ToString(),
                status,
                ingestionStatus,
                updatedAt = updatedAt.ToUniversalTime(),
                userId = userId?.ToString()
            });
            await _redis.GetSubscriber().PublishAsync(RedisChannel.Literal(RealtimeChannel), payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish document lifecycle event {EventType}. DocumentId: {DocumentId}",
                eventType,
                documentId);
        }
    }

    public async Task PublishEmbeddingIndexRequestAsync(
        Guid documentId,
        Guid workspaceId,
        string fullText,
        bool externalLlmAllowed,
        CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var jobId = Guid.NewGuid().ToString();

            // Simple chunker (~1000 chars per chunk)
            var chunks = new System.Collections.Generic.List<object>();
            int chunkSize = 1000;
            int offset = 0;
            int chunkIdx = 0;
            string text = fullText ?? string.Empty;

            if (text.Length == 0)
            {
                text = "Empty Document";
            }

            while (offset < text.Length)
            {
                int len = Math.Min(chunkSize, text.Length - offset);
                string chunkText = text.Substring(offset, len);
                chunks.Add(new
                {
                    id = $"{documentId}_chunk_{chunkIdx}",
                    text = chunkText,
                    metadata = new { chunk_index = chunkIdx, document_id = documentId.ToString() }
                });
                offset += len;
                chunkIdx++;
            }

            var chunksJson = System.Text.Json.JsonSerializer.Serialize(chunks);

            await db.StreamAddAsync("embedding:index_requests", new NameValueEntry[]
            {
                new NameValueEntry("job_id", jobId),
                new NameValueEntry("workspace_id", workspaceId.ToString()),
                new NameValueEntry("collection_id", "warptalk_workspace_documents"),
                new NameValueEntry("source_type", "workspace_document"),
                new NameValueEntry("source_id", documentId.ToString()),
                new NameValueEntry("chunks_json", chunksJson),
                new NameValueEntry("external_llm_allowed", externalLlmAllowed ? "true" : "false"),
                new NameValueEntry("ai_retrieval_allowed", "true"),
                new NameValueEntry("retention_state", "active"),
                new NameValueEntry("deletion_state", "active"),
                new NameValueEntry("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString())
            }, maxLength: StreamMaxLength, useApproximateMaxLength: true);

            _logger.LogInformation("Published EmbeddingIndexRequest to Redis Stream embedding:index_requests for DocumentId: {DocumentId}, Chunks: {ChunkCount}", documentId, chunkIdx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish EmbeddingIndexRequest to Redis Stream. DocumentId: {DocumentId}", documentId);
        }
    }
}
