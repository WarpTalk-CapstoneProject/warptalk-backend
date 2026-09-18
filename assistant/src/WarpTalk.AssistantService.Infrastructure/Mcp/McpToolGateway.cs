using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Exceptions;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <summary>
/// Speaks the Model Context Protocol to a remote server over streamable HTTP, on behalf of one
/// user's connection.
/// </summary>
/// <remarks>
/// Exactly three methods are used - <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c> - which
/// is everything a catalog row needs to become working tools. Anything richer (resources, prompts,
/// sampling, server notifications) is out of scope until a plugin needs it.
/// <para>
/// WT-710: every exchange is a proper MCP session, opened and closed within the one call. The
/// lifecycle is the spec's: <c>initialize</c>, then <c>notifications/initialized</c>, then the
/// request, carrying the <c>Mcp-Session-Id</c> the server issued and the negotiated
/// <c>MCP-Protocol-Version</c>, and finally a <c>DELETE</c> to end the session. Before this each
/// request went out bare, and a server that keeps sessions - which the spec allows and many hosted
/// servers do - refused <c>tools/call</c> because it had never seen the session it belonged to.
/// </para>
/// <para>
/// A session is not kept between calls. A stateless backend cannot pin one to a replica, a
/// server may expire it at any time, and the price is one extra round trip per exchange. It also
/// means server-initiated <c>notifications/tools/list_changed</c> never reaches us, which is why the
/// tool set is a cache refreshed on connect rather than a live subscription.
/// </para>
/// </remarks>
public class McpToolGateway : IMcpToolGateway
{
    private const string ProtocolVersion = "2026-07-28";
    private const string SessionIdHeader = "Mcp-Session-Id";
    private const string ProtocolVersionHeader = "MCP-Protocol-Version";

    /// <summary>
    /// How many <c>tools/list</c> pages one sync will follow. A server that paginates forever -
    /// a cursor bug, or hostility - must not hold a connect open indefinitely.
    /// </summary>
    private const int MaxToolPages = 20;

    /// <summary>
    /// How long a response body may take once its headers have arrived. <see cref="HttpClient.Timeout"/>
    /// stops counting at the headers when the body is streamed, so an SSE response that never sends
    /// its event would otherwise wait on the caller's token alone.
    /// </summary>
    private static readonly TimeSpan ResponseBodyTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly IPluginCredentialProtector _credentialProtector;
    private readonly ILogger<McpToolGateway> _logger;

    public McpToolGateway(
        HttpClient httpClient,
        IPluginCredentialProtector credentialProtector,
        ILogger<McpToolGateway> logger)
    {
        _httpClient = httpClient;
        _credentialProtector = credentialProtector;
        _logger = logger;
    }

    public async Task<IReadOnlyList<McpToolDescriptorDto>> ListToolsAsync(
        PluginDefinitionDto plugin,
        PluginConnection connection,
        CancellationToken ct = default)
    {
        var serverUrl = RequireServerUrl(plugin);
        var accessToken = _credentialProtector.Unprotect(connection.EncryptedAccessToken!);

        var session = await OpenSessionAsync(serverUrl, accessToken, ct);
        try
        {
            var descriptors = new List<McpToolDescriptorDto>();
            string? cursor = null;

            // WT-710: every page, not the first. A server with more tools than fit on one page
            // otherwise had the rest silently missing from WarpBot.
            for (var page = 0; page < MaxToolPages; page++)
            {
                var parameters = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
                var result = await session.RequestAsync("tools/list", parameters, ct);

                if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                {
                    descriptors.AddRange(tools.EnumerateArray()
                        .Select(tool => ToDescriptor(plugin.Key, plugin.RequiredScopes, tool))
                        .Where(tool => tool is not null)
                        .Select(tool => tool!));
                }

                cursor = ReadString(result, "nextCursor");
                if (string.IsNullOrEmpty(cursor)) break;
            }

            return descriptors;
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    public async Task<McpToolExecutionResult> ExecuteAsync(
        PluginDefinitionDto plugin,
        McpToolDescriptorDto tool,
        PluginConnection connection,
        McpToolExecutionRequest request,
        CancellationToken ct = default)
    {
        var serverUrl = RequireServerUrl(plugin);
        var accessToken = _credentialProtector.Unprotect(connection.EncryptedAccessToken!);

        McpSession? session = null;
        try
        {
            session = await OpenSessionAsync(serverUrl, accessToken, ct);

            var callParams = new JsonObject
            {
                ["name"] = tool.Name,
                ["arguments"] = request.Arguments?.DeepClone() ?? new JsonObject(),
            };

            var result = await session.RequestAsync("tools/call", callParams, ct);

            // MCP reports tool-level failure inside a successful response via isError, distinct
            // from a protocol or transport error. Collapsing the two would tell a user their
            // connection is broken when the tool merely refused the arguments.
            var isError = result.TryGetProperty("isError", out var flag)
                && flag.ValueKind == JsonValueKind.True;

            var content = result.TryGetProperty("content", out var payload)
                ? payload.GetRawText()
                : "[]";

            return isError
                ? Failure(
                    PluginConstants.ErrorCodes.ToolError,
                    $"The tool reported an error: {Summarise(content)}")
                : Success(new JsonObject { ["content"] = JsonNode.Parse(content) ?? new JsonArray() });
        }
        catch (PluginProviderException e)
        {
            return Failure(e.ErrorCode, e.Message);
        }
        catch (McpSessionExpiredException)
        {
            // Expired again straight after a fresh initialize. Nothing on our side will change that.
            return Failure(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                "The plugin's server kept rejecting its own session.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation(e, "MCP server for plugin {PluginKey} was unreachable.", plugin.Key);
            return Failure(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                "The plugin's server could not be reached.");
        }
        finally
        {
            if (session is not null) await session.CloseAsync();
        }
    }

    // ---- session lifecycle -------------------------------------------------------------------

    private static JsonObject InitializeParams() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject
        {
            ["name"] = "WarpTalk",
            ["version"] = "1.0.0",
        },
    };

    /// <summary>
    /// <c>initialize</c> then <c>notifications/initialized</c>, keeping what the server issued.
    /// </summary>
    private async Task<McpSession> OpenSessionAsync(string serverUrl, string accessToken, CancellationToken ct)
    {
        var session = new McpSession(this, serverUrl, accessToken);
        await session.InitializeAsync(ct);
        return session;
    }

    /// <summary>One MCP session: the server's session id and the protocol version both sides agreed on.</summary>
    private sealed class McpSession(McpToolGateway gateway, string serverUrl, string accessToken)
    {
        private string? _sessionId;
        private string? _protocolVersion;

        public async Task InitializeAsync(CancellationToken ct)
        {
            _sessionId = null;
            _protocolVersion = null;

            var initialized = await gateway.SendRequestAsync(
                serverUrl, accessToken, sessionId: null, protocolVersion: null, "initialize", InitializeParams(), ct);

            _sessionId = initialized.SessionId;

            // The server answers with the version it will speak - ours if it supports it, otherwise
            // its own. That, not what we asked for, is what every later request declares.
            _protocolVersion = ReadString(initialized.Result, "protocolVersion") ?? ProtocolVersion;

            await gateway.SendNotificationAsync(
                serverUrl, accessToken, _sessionId, _protocolVersion, "notifications/initialized", ct);
        }

        public async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
        {
            try
            {
                return (await gateway.SendRequestAsync(
                    serverUrl, accessToken, _sessionId, _protocolVersion, method, parameters, ct)).Result;
            }
            catch (McpSessionExpiredException) when (_sessionId is not null)
            {
                // The spec's instruction for a 404 on a request carrying a session id: the session is
                // gone, start a new one. Once - a server that expires every session immediately is
                // not one a retry loop can fix.
                await InitializeAsync(ct);
                return (await gateway.SendRequestAsync(
                    serverUrl, accessToken, _sessionId, _protocolVersion, method, parameters, ct)).Result;
            }
        }

        /// <summary>
        /// Ends the session at the server, best effort. A server may answer 405 to say clients cannot
        /// end sessions, and a failure here must never turn a finished call into a failed one.
        /// </summary>
        public async Task CloseAsync()
        {
            if (_sessionId is null) return;

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Delete, serverUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.TryAddWithoutValidation(SessionIdHeader, _sessionId);
                if (_protocolVersion is not null)
                    request.Headers.TryAddWithoutValidation(ProtocolVersionHeader, _protocolVersion);

                using var _ = await gateway._httpClient.SendAsync(request, timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                gateway._logger.LogDebug(e, "Ending MCP session at {ServerUrl} failed; it will expire on its own.", serverUrl);
            }
        }
    }

    // ---- JSON-RPC over streamable HTTP -------------------------------------------------------

    private readonly record struct McpResponse(JsonElement Result, string? SessionId);

    private async Task<McpResponse> SendRequestAsync(
        string serverUrl,
        string accessToken,
        string? sessionId,
        string? protocolVersion,
        string method,
        JsonObject parameters,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            // Cloned: a JSON node belongs to one parent, and a session retry sends the same
            // parameters in a second envelope.
            ["params"] = parameters.DeepClone(),
        };

        using var request = BuildPost(serverUrl, accessToken, sessionId, protocolVersion, envelope);

        // Headers first: the body may be an SSE stream the server keeps open after it has answered,
        // and buffering it would wait for a close that never comes.
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        ThrowForStatus(response, method, sessionId);

        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyTimeout.CancelAfter(ResponseBodyTimeout);

        JsonElement result;
        try
        {
            result = IsEventStream(response)
                ? await ReadEventStreamAsync(response, id, method, bodyTimeout.Token)
                : ParseEnvelope(await response.Content.ReadAsStringAsync(bodyTimeout.Token), id, method);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PluginProviderException(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                $"The plugin's server did not finish answering '{method}' in time.");
        }

        var issuedSessionId = response.Headers.TryGetValues(SessionIdHeader, out var values)
            ? values.FirstOrDefault()
            : null;

        return new McpResponse(result, issuedSessionId ?? sessionId);
    }

    private async Task SendNotificationAsync(
        string serverUrl,
        string accessToken,
        string? sessionId,
        string? protocolVersion,
        string method,
        CancellationToken ct)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };

        using var request = BuildPost(serverUrl, accessToken, sessionId, protocolVersion, envelope);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 202 Accepted is the spec's answer to a notification; a body, if any, carries nothing we need.
        ThrowForStatus(response, method, sessionId);
    }

    private static HttpRequestMessage BuildPost(
        string serverUrl,
        string accessToken,
        string? sessionId,
        string? protocolVersion,
        JsonObject envelope)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, serverUrl)
        {
            Content = JsonContent.Create(envelope),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Streamable HTTP lets a server answer with either a single JSON body or an SSE stream;
        // advertising both is what keeps a compliant server from refusing outright.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (sessionId is not null) request.Headers.TryAddWithoutValidation(SessionIdHeader, sessionId);
        if (protocolVersion is not null) request.Headers.TryAddWithoutValidation(ProtocolVersionHeader, protocolVersion);

        return request;
    }

    private static void ThrowForStatus(HttpResponseMessage response, string method, string? sessionId)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            // The orchestrator turns this into one refresh-and-retry before it reaches a user.
            throw new PluginProviderException(
                PluginConstants.ErrorCodes.ConnectionRequired,
                "The plugin's server rejected the stored credentials.");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new PluginProviderException(
                PluginConstants.ErrorCodes.MissingScope,
                "The connection does not carry the scopes this tool needs.");
        }

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            throw new PluginProviderException(
                PluginConstants.ErrorCodes.ProviderRateLimited,
                "The plugin's server is rate limiting requests.");
        }

        if (response.StatusCode is HttpStatusCode.NotFound && sessionId is not null)
            throw new McpSessionExpiredException();

        if (!response.IsSuccessStatusCode)
        {
            throw new PluginProviderException(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                $"The plugin's server answered {(int)response.StatusCode} to '{method}'.");
        }
    }

    private static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads SSE events until one carries the response to <paramref name="id"/>.
    /// </summary>
    /// <remarks>
    /// The stream may carry server notifications and requests before the response, and may stay
    /// open after it; neither is a reason to wait. Events are assembled per the SSE format - every
    /// <c>data:</c> line up to a blank line - rather than by picking the last <c>data:</c> line of a
    /// body read to the end, which is what made an open stream hang.
    /// </remarks>
    private static async Task<JsonElement> ReadEventStreamAsync(
        HttpResponseMessage response,
        string id,
        string method,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);

            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var match = TryMatchResponse(data.ToString(), id, method);
                    if (match is not null) return match.Value;
                    data.Clear();
                }

                if (line is null) break;
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                var value = line["data:".Length..];
                data.Append(value.StartsWith(' ') ? value[1..] : value);
            }
            // event:, id:, retry: and comments carry nothing a request/response exchange needs.
        }

        throw new PluginProviderException(
            PluginConstants.ErrorCodes.ProviderUnavailable,
            $"The plugin's server closed its event stream without answering '{method}'.");
    }

    /// <summary>The event's result when it is the response to <paramref name="id"/>; null for anything else on the stream.</summary>
    private static JsonElement? TryMatchResponse(string json, string id, string method)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasId(root, id)) return null;
            return ReadEnvelope(root, method);
        }
    }

    /// <summary>A plain JSON body: the response itself, or a batch holding it.</summary>
    private static JsonElement ParseEnvelope(string json, string id, string method)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new PluginProviderException(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                $"The plugin's server answered '{method}' with something that is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var entry = root.EnumerateArray().FirstOrDefault(element =>
                    element.ValueKind == JsonValueKind.Object && HasId(element, id));
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new PluginProviderException(
                        PluginConstants.ErrorCodes.ProviderUnavailable,
                        $"The plugin's server did not include an answer to '{method}'.");
                }

                return ReadEnvelope(entry, method);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PluginProviderException(
                    PluginConstants.ErrorCodes.ProviderUnavailable,
                    $"The plugin's server answered '{method}' with something that is not a JSON-RPC response.");
            }

            // A response that names a different request is an answer to something else. A response
            // with no id at all is tolerated: some servers leave it off an unambiguous single reply.
            if (root.TryGetProperty("id", out _) && !HasId(root, id))
            {
                throw new PluginProviderException(
                    PluginConstants.ErrorCodes.ProviderUnavailable,
                    $"The plugin's server answered '{method}' with the response to a different request.");
            }

            return ReadEnvelope(root, method);
        }
    }

    private static bool HasId(JsonElement element, string id) =>
        element.TryGetProperty("id", out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), id, StringComparison.Ordinal);

    private static JsonElement ReadEnvelope(JsonElement root, string method)
    {
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text)
                ? text.GetString()
                : "no message";

            throw new PluginProviderException(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                $"The plugin's server refused '{method}': {message}");
        }

        // WT-710: a response with neither result nor error used to come back as a default element,
        // and the first TryGetProperty on it threw - a 500 with no audit row. It is a protocol
        // failure and is reported as one.
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new PluginProviderException(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                $"The plugin's server answered '{method}' without a result.");
        }

        return result.Clone();
    }

    // ---- mapping -----------------------------------------------------------------------------

    /// <summary>
    /// Maps one <c>tools/list</c> entry onto the descriptor the rest of the system already speaks.
    /// </summary>
    /// <remarks>
    /// Every remote tool is registered as <see cref="PluginConstants.ToolEffect.Write"/> unless its
    /// own annotations say it is read-only. MCP's <c>readOnlyHint</c> is a hint from a server we do
    /// not control, so the safe reading of silence is "this may write" - which routes the call
    /// through the confirmation gate rather than around it.
    /// </remarks>
    private static McpToolDescriptorDto? ToDescriptor(
        string pluginKey,
        IReadOnlyList<string> pluginScopes,
        JsonElement tool)
    {
        var name = ReadString(tool, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var readOnly = tool.TryGetProperty("annotations", out var annotations)
            && annotations.ValueKind == JsonValueKind.Object
            && annotations.TryGetProperty("readOnlyHint", out var hint)
            && hint.ValueKind == JsonValueKind.True;

        var parameters = tool.TryGetProperty("inputSchema", out var schema)
            && JsonNode.Parse(schema.GetRawText()) is JsonObject parsed
                ? parsed
                : new JsonObject();

        return new McpToolDescriptorDto(
            Name: name,
            PluginKey: pluginKey,
            Label: ReadString(tool, "title") ?? name,
            Description: ReadString(tool, "description") ?? string.Empty,
            Effect: readOnly ? PluginConstants.ToolEffect.Read : PluginConstants.ToolEffect.Write,
            // The row's own scopes, not an empty list.
            //
            // tools/list carries no scope information - MCP has no field for it - so there is
            // nothing per-tool to read here. Returning nothing, though, quietly disabled the
            // orchestrator's scope gate for every MCP plugin at once: it compares a tool's
            // RequiredScopes against the connection's granted set, and an empty list is satisfied
            // by any grant at all. A user who declined a permission on the consent screen was
            // recorded as `partial` and then allowed to run every tool the plugin has, with the
            // remote server's own 403 as the only thing left standing between them.
            //
            // Falling back to the plugin's declared scopes makes the gate mean what it says: this
            // row asked for these scopes, so its tools need them. It is coarser than per-tool
            // scopes would be - it refuses a read tool when a write scope is missing - but it
            // refuses in the safe direction, and it is the only scope statement the catalog
            // actually has.
            RequiredScopes: pluginScopes,
            Parameters: parameters);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequireServerUrl(PluginDefinitionDto plugin) =>
        string.IsNullOrWhiteSpace(plugin.McpServerUrl)
            ? throw new InvalidOperationException($"Plugin '{plugin.Key}' has no MCP server URL.")
            : plugin.McpServerUrl;

    private static McpToolExecutionResult Success(JsonObject data) =>
        new(true, null, null, data, null, null);

    private static McpToolExecutionResult Failure(string errorCode, string message) =>
        new(false, errorCode, message, null, null, null);

    private static string Summarise(string value) =>
        value.Length <= 300 ? value : value[..300] + "...";

    /// <summary>A 404 on a request that carried a session id: the server has forgotten the session.</summary>
    private sealed class McpSessionExpiredException() : Exception("The MCP session has expired.");
}
