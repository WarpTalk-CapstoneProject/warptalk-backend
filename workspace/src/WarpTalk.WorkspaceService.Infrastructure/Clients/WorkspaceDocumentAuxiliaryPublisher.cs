using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public sealed class WorkspaceDocumentAuxiliaryPublisher(
    IConnectionMultiplexer redis,
    ILogger<WorkspaceDocumentAuxiliaryPublisher> logger)
{
    private const string RealtimeChannel = "warptalk:documents:events";
    private const int StreamMaxLength = 10000;

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
            await redis.GetSubscriber().PublishAsync(
                RedisChannel.Literal(RealtimeChannel),
                payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
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
            var chunks = new List<object>();
            const int chunkSize = 1000;
            var offset = 0;
            var chunkIndex = 0;
            var text = string.IsNullOrEmpty(fullText) ? "Empty Document" : fullText;

            while (offset < text.Length)
            {
                var length = Math.Min(chunkSize, text.Length - offset);
                chunks.Add(new
                {
                    id = $"{documentId}_chunk_{chunkIndex}",
                    text = text.Substring(offset, length),
                    metadata = new
                    {
                        chunk_index = chunkIndex,
                        document_id = documentId.ToString()
                    }
                });
                offset += length;
                chunkIndex++;
            }

            await redis.GetDatabase().StreamAddAsync(
                "embedding:index_requests",
                new NameValueEntry[]
                {
                    new("job_id", Guid.NewGuid().ToString()),
                    new("workspace_id", workspaceId.ToString()),
                    new("collection_id", "warptalk_workspace_documents"),
                    new("source_type", "workspace_document"),
                    new("source_id", documentId.ToString()),
                    new("chunks_json", JsonSerializer.Serialize(chunks)),
                    new("external_llm_allowed", externalLlmAllowed ? "true" : "false"),
                    new("ai_retrieval_allowed", "true"),
                    new("retention_state", "active"),
                    new("deletion_state", "active"),
                    new("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString())
                },
                maxLength: StreamMaxLength,
                useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to publish EmbeddingIndexRequest. DocumentId: {DocumentId}",
                documentId);
        }
    }
}
