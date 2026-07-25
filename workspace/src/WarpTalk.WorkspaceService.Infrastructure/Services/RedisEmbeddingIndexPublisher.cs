using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Services;

public class RedisEmbeddingIndexPublisher : IEmbeddingIndexPublisher
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDocumentTextChunker _chunker;
    private readonly ILogger<RedisEmbeddingIndexPublisher> _logger;

    private const int EmbeddingChunkCharLimit = 2000;
    private const string IndexRequestsStreamKey = "embedding:index_requests";

    public RedisEmbeddingIndexPublisher(
        IConnectionMultiplexer redis,
        IDocumentTextChunker chunker,
        ILogger<RedisEmbeddingIndexPublisher> logger)
    {
        _redis = redis;
        _chunker = chunker;
        _logger = logger;
    }

    public async Task<string?> PublishEmbeddingIndexRequestAsync(
        WorkspaceDocument document,
        string fullText,
        bool externalLlmAllowed,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fullText))
        {
            return null;
        }

        var chunks = _chunker.ChunkText(fullText, EmbeddingChunkCharLimit)
            .Select((text, index) => new
            {
                id = Guid.NewGuid().ToString(),
                text,
                metadata = new
                {
                    document_id = document.Id.ToString(),
                    document_name = document.Name,
                    chunk_index = index,
                },
            })
            .ToList();

        if (chunks.Count == 0)
        {
            return null;
        }

        var jobId = Guid.NewGuid().ToString();

        var entries = new NameValueEntry[]
        {
            new("job_id", jobId),
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
        await db.StreamAddAsync(IndexRequestsStreamKey, entries, maxLength: 10000, useApproximateMaxLength: true);

        _logger.LogInformation("Published embedding index request for document {DocumentId}. JobId: {JobId}, Chunks: {ChunkCount}, Collection: {CollectionId}",
            document.Id, jobId, chunks.Count, $"workspace_{document.WorkspaceId}");

        return jobId;
    }
}
