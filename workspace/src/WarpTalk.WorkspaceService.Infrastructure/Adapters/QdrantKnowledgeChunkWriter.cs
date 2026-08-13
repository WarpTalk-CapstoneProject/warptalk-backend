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
/// Corrects and removes indexed chunks in Qdrant over its REST API.
///
/// The write counterpart to <see cref="QdrantKnowledgeChunkReader"/>, and deliberately a
/// separate class rather than more methods on it: the reader is used by the listing, which is
/// open to Owner and Admin, while this is only ever reached after a stricter check. Keeping
/// them apart means nothing that only meant to read can reach a delete by holding the wrong
/// interface.
///
/// Both operations are idempotent. Qdrant answers a delete of an absent point with success, and
/// setting a payload on one 404s here — which the caller has already ruled out by reading the
/// chunk first, and which is in any case the same outcome the caller wanted.
/// </summary>
public class QdrantKnowledgeChunkWriter : IKnowledgeChunkWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<QdrantKnowledgeChunkWriter> _logger;

    public QdrantKnowledgeChunkWriter(HttpClient httpClient, ILogger<QdrantKnowledgeChunkWriter> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task SetAnnotationAsync(
        Guid workspaceId,
        string chunkId,
        KnowledgeChunkAnnotation annotation,
        CancellationToken ct = default)
    {
        var collection = $"workspace_{workspaceId}";

        // set_payload MERGES: keys named here are replaced, every other key — text, the
        // provenance, retention_state, the workspace id itself — is left exactly as the
        // indexer wrote it. A whole-payload overwrite would silently drop whatever a producer
        // this class has never heard of had stored beside them.
        //
        // A null fact is written as null rather than by deleting the key: "the extracted fact
        // is wrong, and there is no replacement" is a statement an Owner is entitled to make,
        // and the reader treats a null and an absent key identically.
        var request = new
        {
            payload = new Dictionary<string, object?>
            {
                ["fact"] = annotation.Fact,
                ["fact_category"] = annotation.FactCategory,
                ["ai_retrieval"] = annotation.AiRetrieval,
            },
            points = new[] { PointId(chunkId) },
        };

        using var response = await _httpClient.PostAsJsonAsync(
            $"collections/{collection}/points/payload?wait=true", request, SerializerOptions, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The collection is gone, which means every chunk in it is too. Nothing left to
            // correct, and nothing here that a caller could do about it.
            _logger.LogWarning(
                "Knowledge annotation skipped: collection {Collection} does not exist.", collection);
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(Guid workspaceId, string chunkId, CancellationToken ct = default)
    {
        var collection = $"workspace_{workspaceId}";
        var request = new { points = new[] { PointId(chunkId) } };

        using var response = await _httpClient.PostAsJsonAsync(
            $"collections/{collection}/points/delete?wait=true", request, SerializerOptions, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Nothing was ever indexed for this workspace, so the chunk the caller wants gone
            // is already gone. That is the outcome they asked for.
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Qdrant ids are a uuid or an unsigned integer and it rejects the wrong JSON type rather
    /// than coercing, so the type comes from the value. Same rule as the reader's.
    /// </summary>
    private static object PointId(string chunkId)
        => ulong.TryParse(chunkId, out var numeric) ? numeric : chunkId;
}
