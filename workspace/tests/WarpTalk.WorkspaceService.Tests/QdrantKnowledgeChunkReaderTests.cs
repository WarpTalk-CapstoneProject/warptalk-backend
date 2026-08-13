using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class QdrantKnowledgeChunkReaderTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();

    private static (QdrantKnowledgeChunkReader Reader, RecordingHandler Handler) Build(
        HttpStatusCode status,
        string body)
    {
        var handler = new RecordingHandler(status, body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://qdrant.test/") };
        return (
            new QdrantKnowledgeChunkReader(
                client, Substitute.For<ILogger<QdrantKnowledgeChunkReader>>()),
            handler);
    }

    [Fact]
    public async Task ScrollAsync_ReturnsAnEmptyPage_WhenTheCollectionDoesNotExist()
    {
        // A workspace that has never uploaded anything has no collection. That is a normal
        // state, and the same contract the Python VectorStore holds — not a 500.
        var (reader, _) = Build(HttpStatusCode.NotFound, "{\"status\":\"error\"}");

        var page = await reader.ScrollAsync(
            _workspaceId, new KnowledgeChunkFilter(null, null), 50, null);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ScrollAsync_ReadsTheWorkspacesOwnCollection_AndFiltersOnWorkspaceIdAsWell()
    {
        var (reader, handler) = Build(HttpStatusCode.OK, EmptyResult);

        await reader.ScrollAsync(_workspaceId, new KnowledgeChunkFilter(null, null), 50, null);

        Assert.Equal(
            $"http://qdrant.test/collections/workspace_{_workspaceId}/points/scroll",
            handler.LastRequestUri);

        // Belt and braces: the collection already partitions by workspace, and the payload
        // filter means a renamed or shared collection still cannot return another
        // workspace's rows.
        using var sent = JsonDocument.Parse(handler.LastRequestBody!);
        var must = sent.RootElement.GetProperty("filter").GetProperty("must");
        Assert.Contains(
            must.EnumerateArray(),
            condition => condition.GetProperty("key").GetString() == "workspace_id"
                && condition.GetProperty("match").GetProperty("value").GetString()
                    == _workspaceId.ToString());
    }

    [Fact]
    public async Task ScrollAsync_SendsOnlyTheFiltersThatWereAskedFor()
    {
        var (reader, handler) = Build(HttpStatusCode.OK, EmptyResult);

        await reader.ScrollAsync(
            _workspaceId, new KnowledgeChunkFilter(["document"], "risk"), 50, null);

        using var sent = JsonDocument.Parse(handler.LastRequestBody!);
        var keys = sent.RootElement.GetProperty("filter").GetProperty("must")
            .EnumerateArray()
            .Select(condition => condition.GetProperty("key").GetString())
            .ToList();
        Assert.Equal(new[] { "workspace_id", "source_type", "fact_category" }, keys);

        var (readerTwo, handlerTwo) = Build(HttpStatusCode.OK, EmptyResult);
        await readerTwo.ScrollAsync(
            _workspaceId, new KnowledgeChunkFilter(null, null), 50, null);

        using var plain = JsonDocument.Parse(handlerTwo.LastRequestBody!);
        var plainKeys = plain.RootElement.GetProperty("filter").GetProperty("must")
            .EnumerateArray()
            .Select(condition => condition.GetProperty("key").GetString())
            .ToList();
        Assert.Equal(new[] { "workspace_id" }, plainKeys);
    }

    [Fact]
    public async Task ScrollAsync_MatchesAnyWhenOneCategoryCoversSeveralStoredTypes()
    {
        // "Glossary" is two producers — GlossaryService and GlobalGlossaryService — writing
        // different source types. Sent as two separate musts they would AND together and match
        // nothing at all, so the tab would look like an empty glossary.
        var (reader, handler) = Build(HttpStatusCode.OK, EmptyResult);

        await reader.ScrollAsync(
            _workspaceId,
            new KnowledgeChunkFilter(["glossary_term", "global_glossary_term"], null),
            50,
            null);

        using var sent = JsonDocument.Parse(handler.LastRequestBody!);
        var sourceType = sent.RootElement.GetProperty("filter").GetProperty("must")
            .EnumerateArray()
            .Single(condition => condition.GetProperty("key").GetString() == "source_type");
        var any = sourceType.GetProperty("match").GetProperty("any")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        Assert.Equal(new[] { "glossary_term", "global_glossary_term" }, any);
    }

    [Fact]
    public async Task ScrollAsync_NamesAGlossaryRowAfterItsTerm()
    {
        // Glossary producers predate `source_title` and write `source_term`. Without the
        // fallback every glossary row would render as the generic word "Glossary term".
        var (reader, _) = Build(HttpStatusCode.OK, """
        {
          "result": {
            "points": [
              {
                "id": "3f",
                "payload": {
                  "chunk_id": "3f",
                  "source_type": "glossary_term",
                  "text": "warp → dịch: real-time translation",
                  "source_term": "warp",
                  "retention_state": "active",
                  "ai_retrieval": true
                }
              }
            ],
            "next_page_offset": null
          }
        }
        """);

        var page = await reader.ScrollAsync(
            _workspaceId, new KnowledgeChunkFilter(null, null), 50, null);

        Assert.Equal("warp", Assert.Single(page.Items).SourceTitle);
    }

    [Fact]
    public async Task ScrollAsync_ExcludesSourceTypesTheCallerRefused()
    {
        var (reader, handler) = Build(HttpStatusCode.OK, EmptyResult);

        await reader.ScrollAsync(
            _workspaceId, new KnowledgeChunkFilter(null, null, ["transcript"]), 50, null);

        using var sent = JsonDocument.Parse(handler.LastRequestBody!);
        var mustNot = sent.RootElement.GetProperty("filter").GetProperty("must_not")
            .EnumerateArray()
            .Select(condition => (
                condition.GetProperty("key").GetString(),
                condition.GetProperty("match").GetProperty("value").GetString()))
            .ToList();
        Assert.Equal([("source_type", "transcript")], mustNot);
    }

    [Fact]
    public async Task ScrollAsync_OmitsMustNotEntirelyWhenNothingIsExcluded()
    {
        // Qdrant is lenient about an empty must_not, but sending one makes every request read
        // as though an exclusion were in play — the next person debugging a missing row would
        // start with the wrong suspect.
        var (reader, handler) = Build(HttpStatusCode.OK, EmptyResult);

        await reader.ScrollAsync(_workspaceId, new KnowledgeChunkFilter(null, null), 50, null);

        using var sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(
            sent.RootElement.GetProperty("filter").TryGetProperty("must_not", out _));
    }

    [Fact]
    public async Task ScrollAsync_MapsADocumentChunkPayload()
    {
        var (reader, _) = Build(HttpStatusCode.OK, """
        {
          "result": {
            "points": [
              {
                "id": "0d3189db-0000-0000-0000-000000000000",
                "payload": {
                  "chunk_id": "0d3189db",
                  "source_type": "document",
                  "text": "Payment terms are net 30 from invoice date.",
                  "fact": "Payment terms are net 30",
                  "fact_category": "requirement",
                  "document_id": "e2b69a00",
                  "document_name": "file-sample_100kB",
                  "chunk_index": 3,
                  "retention_state": "active",
                  "deletion_state": "active",
                  "ai_retrieval": true
                }
              }
            ],
            "next_page_offset": null
          },
          "status": "ok"
        }
        """);

        var page = await reader.ScrollAsync(
            _workspaceId, new KnowledgeChunkFilter(null, null), 50, null);

        var chunk = Assert.Single(page.Items);
        Assert.Equal("0d3189db", chunk.ChunkId);
        Assert.Equal("document", chunk.SourceType);
        Assert.Equal("Payment terms are net 30", chunk.Fact);
        Assert.Equal("requirement", chunk.FactCategory);
        Assert.Equal("file-sample_100kB", chunk.DocumentName);
        Assert.Equal(3, chunk.ChunkIndex);
        Assert.True(chunk.AiRetrieval);
        Assert.Null(chunk.SpeakerName);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ScrollAsync_MapsChunksIndexedBeforeTextAndFactsExisted()
    {
        // 200 points in production predate the payload change. They must list as themselves —
        // with nulls — rather than throw or be silently dropped, or an owner sees an empty
        // table and concludes their upload failed.
        var (reader, _) = Build(HttpStatusCode.OK, """
        {
          "result": {
            "points": [
              {
                "id": 17,
                "payload": {
                  "source_type": "transcript",
                  "speaker_name": "Huỳnh Ngọc Kỳ",
                  "start_ms": 126000,
                  "retention_state": "active",
                  "deletion_state": "active",
                  "ai_retrieval": true
                }
              }
            ],
            "next_page_offset": 42
          },
          "status": "ok"
        }
        """);

        var page = await reader.ScrollAsync(
            _workspaceId, new KnowledgeChunkFilter(null, null), 50, null);

        var chunk = Assert.Single(page.Items);
        Assert.Null(chunk.Text);
        Assert.Null(chunk.Fact);
        Assert.Null(chunk.FactCategory);
        Assert.Equal("Huỳnh Ngọc Kỳ", chunk.SpeakerName);
        Assert.Equal(126000, chunk.StartMs);
        // Falls back to the point id when the payload carries no chunk_id.
        Assert.Equal("17", chunk.ChunkId);
        Assert.Equal("42", page.NextCursor);
    }

    [Fact]
    public async Task ScrollAsync_ThrowsWhenTheStoreFails()
    {
        // Distinct from a missing collection: this is the store being broken, and the caller
        // turns it into a 500 rather than an empty workspace.
        var (reader, _) = Build(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ScrollAsync(
            _workspaceId, new KnowledgeChunkFilter(null, null), 50, null));
    }

    // ── FindAsync: the tenancy check the edit and delete paths stand on ────────────────────

    [Fact]
    public async Task FindAsync_RetrievesByIdRatherThanFilteringOnThePayload()
    {
        // Not a scroll with a `chunk_id` filter. Rows indexed before the payload carried
        // `chunk_id` are listed under their POINT id, so a payload filter cannot see them at
        // all — and retrieve-by-id finds both, because the indexer upserts each point under
        // its own chunk id.
        var (reader, handler) = Build(
            HttpStatusCode.OK,
            $"{{\"result\":[{{\"id\":\"chunk-1\",\"payload\":{{\"workspace_id\":\"{_workspaceId}\",\"source_type\":\"document\",\"text\":\"hello\"}}}}]}}");

        var record = await reader.FindAsync(_workspaceId, "chunk-1");

        Assert.Equal(
            $"http://qdrant.test/collections/workspace_{_workspaceId}/points",
            handler.LastRequestUri);
        Assert.NotNull(record);
        Assert.Equal("chunk-1", record!.ChunkId);
        Assert.Equal("hello", record.Text);
    }

    [Fact]
    public async Task FindAsync_SendsANumericIdAsANumber()
    {
        // Qdrant ids are a uuid or an unsigned integer, and it rejects the wrong JSON type
        // rather than coercing. Transcript segments have used numeric ids.
        var (reader, handler) = Build(HttpStatusCode.OK, "{\"result\":[]}");

        await reader.FindAsync(_workspaceId, "42");

        using var sent = JsonDocument.Parse(handler.LastRequestBody!);
        var id = sent.RootElement.GetProperty("ids")[0];
        Assert.Equal(JsonValueKind.Number, id.ValueKind);
        Assert.Equal(42, id.GetInt32());
    }

    [Fact]
    public async Task FindAsync_RefusesAChunkBelongingToAnotherWorkspace()
    {
        // The whole point. Ids are globally unique across a store shared by every workspace,
        // so this is what stops one workspace's chunk id in a URL from reaching another
        // workspace's row — and, through the writer, deleting it.
        var (reader, _) = Build(
            HttpStatusCode.OK,
            "{\"result\":[{\"id\":\"chunk-1\",\"payload\":{\"workspace_id\":\"11111111-1111-1111-1111-111111111111\",\"source_type\":\"document\"}}]}");

        Assert.Null(await reader.FindAsync(_workspaceId, "chunk-1"));
    }

    [Fact]
    public async Task FindAsync_ReturnsNullRatherThanThrowingForTheOrdinaryMisses()
    {
        // A workspace that has never indexed anything (404), an id Qdrant will not even parse
        // (400), and an id that simply is not there. None of the three is a fault, and a row a
        // colleague deleted while this page was open is the most likely cause of all of them.
        foreach (var (status, body) in new[]
                 {
                     (HttpStatusCode.NotFound, "{\"status\":\"error\"}"),
                     (HttpStatusCode.BadRequest, "{\"status\":\"error\"}"),
                     (HttpStatusCode.OK, "{\"result\":[]}"),
                 })
        {
            var (reader, _) = Build(status, body);
            Assert.Null(await reader.FindAsync(_workspaceId, "chunk-1"));
        }
    }

    [Fact]
    public async Task FindAsync_DoesNotCallTheStoreForAnEmptyId()
    {
        var (reader, handler) = Build(HttpStatusCode.OK, "{\"result\":[]}");

        Assert.Null(await reader.FindAsync(_workspaceId, "  "));
        Assert.Null(handler.LastRequestUri);
    }

    private const string EmptyResult =
        "{\"result\":{\"points\":[],\"next_page_offset\":null},\"status\":\"ok\"}";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public string? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        public RecordingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
