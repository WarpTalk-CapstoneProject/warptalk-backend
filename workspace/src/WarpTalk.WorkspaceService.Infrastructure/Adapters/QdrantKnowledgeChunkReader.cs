using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Adapters;

/// <summary>
/// Reads indexed chunks out of Qdrant over its REST scroll API.
///
/// Two things make this narrower than it looks. Chunks are already partitioned one collection
/// per workspace — <c>workspace_{id}</c>, the name <see cref="RedisEmbeddingIndexPublisher"/>
/// writes — so the collection is derived from the route's workspace id and nothing else. The
/// payload filter on <c>workspace_id</c> on top of that is defence in depth: if a collection
/// were ever renamed or shared, the filter still refuses to return another workspace's rows.
///
/// Scroll rather than search because there is no query vector here — the user is listing what
/// exists, not asking a question.
/// </summary>
public class QdrantKnowledgeChunkReader : IKnowledgeChunkReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<QdrantKnowledgeChunkReader> _logger;

    public QdrantKnowledgeChunkReader(HttpClient httpClient, ILogger<QdrantKnowledgeChunkReader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<KnowledgeChunkPage> ScrollAsync(
        Guid workspaceId,
        KnowledgeChunkFilter filter,
        int limit,
        string? cursor,
        CancellationToken ct = default)
    {
        var collection = $"workspace_{workspaceId}";

        var must = new List<object>
        {
            new { key = "workspace_id", match = new { value = workspaceId.ToString() } },
        };
        if (filter.SourceType != null)
        {
            must.Add(new { key = "source_type", match = new { value = filter.SourceType } });
        }
        if (filter.FactCategory != null)
        {
            must.Add(new { key = "fact_category", match = new { value = filter.FactCategory } });
        }

        var request = new
        {
            limit,
            with_payload = true,
            with_vector = false,
            filter = new { must },
            offset = cursor,
        };

        using var response = await _httpClient.PostAsJsonAsync(
            $"collections/{collection}/points/scroll", request, SerializerOptions, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The workspace has never indexed anything, so the collection was never created.
            // That is a normal state — an empty list is the honest answer, and it is the same
            // contract the Python VectorStore holds for a missing collection.
            return new KnowledgeChunkPage(Array.Empty<KnowledgeChunkRecord>(), null);
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ScrollResponse>(SerializerOptions, ct);
        if (body?.Result?.Points == null)
        {
            _logger.LogWarning(
                "Qdrant scroll returned no result body. Collection: {Collection}", collection);
            return new KnowledgeChunkPage(Array.Empty<KnowledgeChunkRecord>(), null);
        }

        var items = new List<KnowledgeChunkRecord>(body.Result.Points.Count);
        foreach (var point in body.Result.Points)
        {
            var payload = point.Payload ?? new Dictionary<string, JsonElement>();
            items.Add(new KnowledgeChunkRecord(
                ChunkId: ReadString(payload, "chunk_id") ?? point.Id?.ToString() ?? string.Empty,
                SourceType: ReadString(payload, "source_type") ?? "unknown",
                Text: ReadString(payload, "text"),
                Fact: ReadString(payload, "fact"),
                FactCategory: ReadString(payload, "fact_category"),
                DocumentId: ReadString(payload, "document_id"),
                DocumentName: ReadString(payload, "document_name"),
                ChunkIndex: ReadInt(payload, "chunk_index"),
                SpeakerName: ReadString(payload, "speaker_name"),
                StartMs: ReadLong(payload, "start_ms"),
                RetentionState: ReadString(payload, "retention_state"),
                DeletionState: ReadString(payload, "deletion_state"),
                AiRetrieval: ReadBool(payload, "ai_retrieval")));
        }

        // next_page_offset is null on the last page; Qdrant ids may be numeric or uuid, and
        // the cursor is opaque to every layer above this one.
        var nextCursor = body.Result.NextPageOffset.HasValue
            && body.Result.NextPageOffset.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? body.Result.NextPageOffset.Value.ToString()
                : null;

        return new KnowledgeChunkPage(items, nextCursor);
    }

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement> payload, string key)
        => payload.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(IReadOnlyDictionary<string, JsonElement> payload, string key)
        => payload.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static long? ReadLong(IReadOnlyDictionary<string, JsonElement> payload, string key)
        => payload.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static bool ReadBool(IReadOnlyDictionary<string, JsonElement> payload, string key)
        => payload.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;

    private sealed class ScrollResponse
    {
        public ScrollResult? Result { get; set; }
    }

    private sealed class ScrollResult
    {
        public List<ScrollPoint> Points { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("next_page_offset")]
        public JsonElement? NextPageOffset { get; set; }
    }

    private sealed class ScrollPoint
    {
        public JsonElement? Id { get; set; }
        public Dictionary<string, JsonElement>? Payload { get; set; }
    }
}
