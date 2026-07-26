using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private const int StreamMaxLength = 10000;
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

        var ingestionRevision = BuildIngestionRevision(document);
        var chunks = _chunker.ChunkText(fullText, EmbeddingChunkCharLimit)
            .Select((text, index) => new
            {
                // A deterministic UUID makes a retry an upsert of the same Qdrant
                // point instead of creating a duplicate vector.
                id = CreateStableChunkId(document.Id, index),
                text,
                metadata = new
                {
                    document_id = document.Id.ToString(),
                    document_name = document.Name,
                    chunk_index = index,
                    ingestion_revision = ingestionRevision,
                },
            })
            .ToList();

        if (chunks.Count == 0)
        {
            return null;
        }

        // The same document revision must map to the same Redis job when the
        // publisher is retried after a timeout.
        var jobId = $"{document.Id:N}:{ingestionRevision}";

        var entries = new NameValueEntry[]
        {
            new("job_id", jobId),
            new("workspace_id", document.WorkspaceId.ToString()),
            new("collection_id", $"workspace_{document.WorkspaceId}"),
            new("source_type", "document"),
            new("source_id", document.Id.ToString()),
            new("chunks_json", JsonSerializer.Serialize(chunks)),
            new("external_llm_allowed", externalLlmAllowed ? "true" : "false"),
            // Reaching this publisher means the security guardrail has approved
            // indexing. AiEligible is intentionally still false until the Qdrant
            // result processor confirms a successful upsert.
            new("ai_retrieval_allowed", "true"),
            new("retention_state", document.RetentionState),
            new("deletion_state", document.DeletedAt == null ? "active" : "deleted"),
            new("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
        };

        var db = _redis.GetDatabase();
        await db.StreamAddAsync(IndexRequestsStreamKey, entries, maxLength: StreamMaxLength, useApproximateMaxLength: true);

        _logger.LogInformation("Published embedding index request for document {DocumentId}. JobId: {JobId}, Chunks: {ChunkCount}, Collection: {CollectionId}",
            document.Id, jobId, chunks.Count, $"workspace_{document.WorkspaceId}");

        return jobId;
    }

    private static string BuildIngestionRevision(WorkspaceDocument document)
    {
        var timestamp = document.UpdatedAt == default ? document.CreatedAt : document.UpdatedAt;
        return timestamp == default ? "initial" : timestamp.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
    }

    private static string CreateStableChunkId(Guid documentId, int chunkIndex)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"warptalk:{documentId:N}:{chunkIndex}"));
        return new Guid(bytes).ToString();
    }
}
