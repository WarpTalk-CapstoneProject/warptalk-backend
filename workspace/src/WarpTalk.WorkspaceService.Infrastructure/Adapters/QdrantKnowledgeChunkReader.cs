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
        // One clause either way. `match.any` is Qdrant's match-one-of, so a category that maps
        // to several stored types (glossary) stays a single condition rather than a nested
        // should-block that would have to be combined with the other musts by hand.
        var sourceTypes = filter.SourceTypes ?? Array.Empty<string>();
        if (sourceTypes.Count == 1)
        {
            must.Add(new { key = "source_type", match = new { value = sourceTypes[0] } });
        }
        else if (sourceTypes.Count > 1)
        {
            must.Add(new { key = "source_type", match = new { any = sourceTypes } });
        }
        if (filter.FactCategory != null)
        {
            must.Add(new { key = "fact_category", match = new { value = filter.FactCategory } });
        }

        // Exclusion is a separate Qdrant clause, not "every other source type in a should" —
        // an allow-list built here would silently drop any source type added later by a
        // producer this adapter has never heard of.
        var mustNot = new List<object>();
        foreach (var excluded in filter.ExcludedSourceTypes ?? Array.Empty<string>())
        {
            mustNot.Add(new { key = "source_type", match = new { value = excluded } });
        }

        var request = new
        {
            limit,
            with_payload = true,
            with_vector = false,
            filter = mustNot.Count == 0
                ? (object)new { must }
                : new { must, must_not = mustNot },
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
            items.Add(ToRecord(point));
        }

        // next_page_offset is null on the last page; Qdrant ids may be numeric or uuid, and
        // the cursor is opaque to every layer above this one.
        var nextCursor = body.Result.NextPageOffset.HasValue
            && body.Result.NextPageOffset.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? body.Result.NextPageOffset.Value.ToString()
                : null;

        return new KnowledgeChunkPage(items, nextCursor);
    }

    public async Task<KnowledgeChunkRecord?> FindAsync(
        Guid workspaceId,
        string chunkId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chunkId)) return null;

        var collection = $"workspace_{workspaceId}";

        // Retrieve by id rather than scrolling with a `chunk_id` filter. The payload's
        // `chunk_id` is written by the indexer, but ScrollAsync falls back to the point id for
        // rows indexed before it was, so a filter on the payload key cannot see those at all —
        // and the id is the same value either way, because the indexer upserts each point
        // under its own chunk id.
        var request = new { ids = new[] { PointId(chunkId) }, with_payload = true, with_vector = false };

        using var response = await _httpClient.PostAsJsonAsync(
            $"collections/{collection}/points", request, SerializerOptions, ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // NotFound: the workspace has never indexed anything. BadRequest: Qdrant rejects an
            // id that is neither a uuid nor an unsigned integer, which is what a hand-typed or
            // stale chunk id looks like. Neither is a fault, and both mean "no such chunk".
            return null;
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<RetrieveResponse>(SerializerOptions, ct);
        var found = body?.Result is { Count: > 0 } ? body.Result[0] : null;
        if (found == null) return null;

        // The collection is already per-workspace, so this can only differ if a collection were
        // renamed or shared. Checking anyway is what keeps "reader returns null for another
        // workspace's chunk" true by construction rather than by deployment layout — the writer
        // trusts this method to be the tenancy check before it deletes anything.
        var payload = found.Payload ?? new Dictionary<string, JsonElement>();
        var owner = ReadString(payload, "workspace_id");
        if (owner != null && !string.Equals(owner, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Refused a knowledge chunk read across workspaces. Collection: {Collection}", collection);
            return null;
        }

        return ToRecord(found);
    }

    /// <summary>
    /// Qdrant ids are a uuid or an unsigned integer, and it rejects the wrong JSON type rather
    /// than coercing. Producers here write uuids, but transcript segments have used numeric ids,
    /// so the type is chosen from the value instead of assumed.
    /// </summary>
    private static object PointId(string chunkId)
        => ulong.TryParse(chunkId, out var numeric) ? numeric : chunkId;

    private static KnowledgeChunkRecord ToRecord(ScrollPoint point)
    {
        var payload = point.Payload ?? new Dictionary<string, JsonElement>();
        return new KnowledgeChunkRecord(
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
            AiRetrieval: ReadBool(payload, "ai_retrieval"),
            // Glossary producers predate `source_title` and write `source_term` instead.
            // Falling back keeps their rows named after the term rather than the generic
            // "Glossary term" the UI would otherwise have to show.
            SourceTitle: ReadString(payload, "source_title")
                ?? ReadString(payload, "source_term"));
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

    private sealed class RetrieveResponse
    {
        public List<ScrollPoint>? Result { get; set; }
    }

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
