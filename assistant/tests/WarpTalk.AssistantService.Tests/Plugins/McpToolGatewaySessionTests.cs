using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Infrastructure.Mcp;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The MCP session lifecycle and response handling of <see cref="McpToolGateway"/>. WT-710.
/// </summary>
/// <remarks>
/// Driven by a small in-memory server that behaves the way hosted MCP servers do: it issues a
/// session id on <c>initialize</c> and refuses any later request that does not carry it. Before
/// WT-710 every one of these servers rejected <c>tools/call</c>.
/// </remarks>
public class McpToolGatewaySessionTests
{
    private const string NegotiatedVersion = "2025-11-25";

    [Fact]
    public async Task ListToolsAsync_RunsTheSessionLifecycle_AndFollowsEveryPage()
    {
        var server = new FakeMcpServer();

        var tools = await Gateway(server).ListToolsAsync(Definition(), Connected());

        Assert.Equal(["first_tool", "second_tool"], tools.Select(tool => tool.Name));
        Assert.Equal(
            ["initialize", "notifications/initialized", "tools/list", "tools/list", "DELETE"],
            server.Calls.Select(call => call.Method));

        // Everything after initialize carries the session the server issued and the version it
        // answered with - not the version we asked for.
        Assert.All(server.Calls.Skip(1), call =>
        {
            Assert.Equal("session-1", call.SessionId);
            Assert.Equal(NegotiatedVersion, call.ProtocolVersion);
        });
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsAsSoonAsTheSseResponseArrives_EvenWhenTheStreamStaysOpen()
    {
        var server = new FakeMcpServer { CallResponse = CallResponseMode.SseThenHang };
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await Gateway(server).ExecuteAsync(Definition(), Tool(), Connected(), Request(), guard.Token);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains("done", result.Data!.ToJsonString());
    }

    [Fact]
    public async Task ExecuteAsync_ReportsAResponseWithoutResult_AsAFailure_NotAnException()
    {
        var server = new FakeMcpServer { CallResponse = CallResponseMode.NoResult };

        var result = await Gateway(server).ExecuteAsync(Definition(), Tool(), Connected(), Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsAToolLevelError_AsToolError()
    {
        var server = new FakeMcpServer { CallResponse = CallResponseMode.IsError };

        var result = await Gateway(server).ExecuteAsync(Definition(), Tool(), Connected(), Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ToolError, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_RefusesTheResponseToADifferentRequest()
    {
        var server = new FakeMcpServer { CallResponse = CallResponseMode.WrongId };

        var result = await Gateway(server).ExecuteAsync(Definition(), Tool(), Connected(), Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_StartsANewSessionOnce_WhenTheServerHasForgottenIt()
    {
        var server = new FakeMcpServer { ExpireFirstSessionOnCall = true };

        var result = await Gateway(server).ExecuteAsync(Definition(), Tool(), Connected(), Request());

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, server.Calls.Count(call => call.Method == "initialize"));
        Assert.Equal("session-2", server.Calls.Last(call => call.Method == "tools/call").SessionId);
    }

    // ---- fixtures ----------------------------------------------------------------------------

    private static McpToolGateway Gateway(FakeMcpServer server)
    {
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("enc:access").Returns("access-token");
        return new McpToolGateway(new HttpClient(server), protector, NullLogger<McpToolGateway>.Instance);
    }

    private static PluginDefinitionDto Definition() =>
        new(
            Id: Guid.NewGuid(),
            Key: "remote_app",
            Provider: "remote_app",
            Label: "Remote App",
            Description: "A remote MCP server.",
            AvatarUrl: null,
            RequiredScopes: Array.Empty<string>(),
            Tools: Array.Empty<McpToolDescriptorDto>(),
            Kind: PluginConstants.PluginKind.Mcp,
            McpServerUrl: "https://remote.test/mcp");

    private static McpToolDescriptorDto Tool() =>
        new("first_tool", "remote_app", "First", "", PluginConstants.ToolEffect.Read, [], new JsonObject());

    private static McpToolExecutionRequest Request() =>
        new(Guid.NewGuid(), "remote_app", "first_tool", new JsonObject { ["q"] = "x" }, null, null, null);

    private static PluginConnection Connected() =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = "remote_app",
            Status = PluginConstants.ConnectionStatus.Connected,
            EncryptedAccessToken = "enc:access",
        };

    private enum CallResponseMode { Json, SseThenHang, NoResult, IsError, WrongId }

    private sealed record RecordedCall(string Method, string? SessionId, string? ProtocolVersion);

    private sealed class FakeMcpServer : HttpMessageHandler
    {
        private int _sessionCounter;
        private string? _liveSession;
        private bool _expired;

        public List<RecordedCall> Calls { get; } = [];
        public CallResponseMode CallResponse { get; init; } = CallResponseMode.Json;
        public bool ExpireFirstSessionOnCall { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var sessionId = Header(request, "Mcp-Session-Id");
            var version = Header(request, "MCP-Protocol-Version");

            if (request.Method == HttpMethod.Delete)
            {
                Calls.Add(new RecordedCall("DELETE", sessionId, version));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var method = body["method"]!.GetValue<string>();
            var id = body["id"]?.GetValue<string>();
            Calls.Add(new RecordedCall(method, sessionId, version));

            if (method == "initialize")
            {
                _liveSession = $"session-{++_sessionCounter}";
                var response = Json(id, new JsonObject
                {
                    ["protocolVersion"] = NegotiatedVersion,
                    ["capabilities"] = new JsonObject(),
                    ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1" },
                });
                response.Headers.Add("Mcp-Session-Id", _liveSession);
                return response;
            }

            if (sessionId != _liveSession) return new HttpResponseMessage(HttpStatusCode.BadRequest);

            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);

            if (method == "tools/list")
            {
                var cursor = body["params"]?["cursor"]?.GetValue<string>();
                return cursor is null
                    ? Json(id, new JsonObject { ["tools"] = new JsonArray { new JsonObject { ["name"] = "first_tool" } }, ["nextCursor"] = "page-2" })
                    : Json(id, new JsonObject { ["tools"] = new JsonArray { new JsonObject { ["name"] = "second_tool" } } });
            }

            if (ExpireFirstSessionOnCall && !_expired)
            {
                _expired = true;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var done = new JsonObject { ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "done" } } };
            return CallResponse switch
            {
                CallResponseMode.NoResult => Raw(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id }),
                CallResponseMode.IsError => Json(id, new JsonObject { ["isError"] = true, ["content"] = new JsonArray() }),
                CallResponseMode.WrongId => Json("someone-else", done),
                CallResponseMode.SseThenHang => Sse(id, done),
                _ => Json(id, done),
            };
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

        private static HttpResponseMessage Json(string? id, JsonObject result) =>
            Raw(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

        private static HttpResponseMessage Raw(JsonObject envelope) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json"),
            };

        private static HttpResponseMessage Sse(string? id, JsonObject result)
        {
            var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/progress", ["params"] = new JsonObject() };
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            var events = $"event: message\ndata: {notification.ToJsonString()}\n\n"
                + $"event: message\ndata: {response.ToJsonString()}\n\n";

            var content = new StreamContent(new OpenEndedStream(Encoding.UTF8.GetBytes(events)));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>Serves its bytes, then blocks like a server holding the stream open.</summary>
    private sealed class OpenEndedStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
