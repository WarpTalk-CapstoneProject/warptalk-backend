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
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The purge a document revoke relies on. The `workspace.document_invalidated` event has no
/// consumer anywhere, so this adapter call is the only thing that actually removes a document's
/// chunks from Qdrant.
/// </summary>
public class QdrantKnowledgeChunkWriterTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();

    private static (QdrantKnowledgeChunkWriter Writer, RecordingHandler Handler) Build(params HttpStatusCode[] statuses)
    {
        var handler = new RecordingHandler(statuses);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://qdrant.test/") };
        return (new QdrantKnowledgeChunkWriter(client, Substitute.For<ILogger<QdrantKnowledgeChunkWriter>>()), handler);
    }

    [Fact]
    public async Task DeleteDocumentChunksAsync_DeletesByFilter_FromBothCollectionsDocumentsAreWrittenTo()
    {
        var (writer, handler) = Build(HttpStatusCode.OK, HttpStatusCode.OK);

        await writer.DeleteDocumentChunksAsync(_workspaceId, _documentId);

        Assert.Equal(
            new[]
            {
                $"http://qdrant.test/collections/workspace_{_workspaceId}/points/delete?wait=true",
                "http://qdrant.test/collections/warptalk_workspace_documents/points/delete?wait=true",
            },
            handler.Requests.Select(r => r.Uri));
    }

    [Fact]
    public async Task DeleteDocumentChunksAsync_MatchesThisDocument_UnderBothSourceTypeSpellings()
    {
        var (writer, handler) = Build(HttpStatusCode.OK, HttpStatusCode.OK);

        await writer.DeleteDocumentChunksAsync(_workspaceId, _documentId);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var must = body.RootElement.GetProperty("filter").GetProperty("must").EnumerateArray().ToList();

        string? ValueOf(string key) => must
            .Single(c => c.GetProperty("key").GetString() == key)
            .GetProperty("match").GetProperty("value").GetString();

        Assert.Equal(_workspaceId.ToString(), ValueOf("workspace_id"));
        Assert.Equal(_documentId.ToString(), ValueOf("source_id"));

        var sourceTypes = must
            .Single(c => c.GetProperty("key").GetString() == "source_type")
            .GetProperty("match").GetProperty("any").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "document", "workspace_document" }, sourceTypes);

        // A delete by point id would need the ids first; a delete with NO filter would empty the
        // workspace. The body must carry the filter and nothing else.
        Assert.False(body.RootElement.TryGetProperty("points", out _));
    }

    [Fact]
    public async Task DeleteDocumentChunksAsync_TreatsAMissingCollectionAsAlreadyDeleted()
    {
        var (writer, handler) = Build(HttpStatusCode.NotFound, HttpStatusCode.NotFound);

        await writer.DeleteDocumentChunksAsync(_workspaceId, _documentId);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DeleteDocumentChunksAsync_SurfacesARealFailure()
    {
        var (writer, _) = Build(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => writer.DeleteDocumentChunksAsync(_workspaceId, _documentId));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses;

        public List<(string? Uri, string? Body)> Requests { get; } = new();

        public RecordingHandler(IEnumerable<HttpStatusCode> statuses)
        {
            _statuses = new Queue<HttpStatusCode>(statuses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.ToString(),
                request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(_statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
