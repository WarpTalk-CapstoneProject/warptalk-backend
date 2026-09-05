using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

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
/// No session is held between calls. A stateless backend cannot keep one across replicas anyway,
/// and MCP allows <c>initialize</c> per exchange; the cost is one extra round trip, the benefit is
/// that any replica can serve any request. It also means server-initiated
/// <c>notifications/tools/list_changed</c> never reaches us, which is why the tool set is a cache
/// refreshed on connect rather than a live subscription.
/// </para>
/// </remarks>
public class McpToolGateway : IMcpToolGateway
{
    private const string ProtocolVersion = "2026-07-28";

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

        await SendAsync(serverUrl, accessToken, "initialize", InitializeParams(), ct);
        var result = await SendAsync(serverUrl, accessToken, "tools/list", new JsonObject(), ct);

        if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return Array.Empty<McpToolDescriptorDto>();

        return tools.EnumerateArray()
            .Select(tool => ToDescriptor(plugin.Key, tool))
            .Where(tool => tool is not null)
            .Select(tool => tool!)
            .ToArray();
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

        try
        {
            await SendAsync(serverUrl, accessToken, "initialize", InitializeParams(), ct);

            var callParams = new JsonObject
            {
                ["name"] = tool.Name,
                ["arguments"] = request.Arguments?.DeepClone() ?? new JsonObject(),
            };

            var result = await SendAsync(serverUrl, accessToken, "tools/call", callParams, ct);

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
                    PluginConstants.ErrorCodes.ProviderUnavailable,
                    $"The tool reported an error: {Summarise(content)}")
                : Success(new JsonObject { ["content"] = JsonNode.Parse(content) ?? new JsonArray() });
        }
        catch (McpProtocolException e)
        {
            return Failure(e.ErrorCode, e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _logger.LogInformation(e, "MCP server for plugin {PluginKey} was unreachable.", plugin.Key);
            return Failure(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                "The plugin's server could not be reached.");
        }
    }

    // ---- JSON-RPC over streamable HTTP -------------------------------------------------------

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

    private async Task<JsonElement> SendAsync(
        string serverUrl,
        string accessToken,
        string method,
        JsonObject parameters,
        CancellationToken ct)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = method,
            ["params"] = parameters,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl)
        {
            Content = JsonContent.Create(envelope),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Streamable HTTP lets a server answer with either a single JSON body or an SSE stream;
        // advertising both is what keeps a compliant server from refusing outright.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            // The orchestrator turns this into one refresh-and-retry before it reaches a user.
            throw new McpProtocolException(
                PluginConstants.ErrorCodes.ConnectionRequired,
                "The plugin's server rejected the stored credentials.");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new McpProtocolException(
                PluginConstants.ErrorCodes.MissingScope,
                "The connection does not carry the scopes this tool needs.");
        }

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            throw new McpProtocolException(
                PluginConstants.ErrorCodes.ProviderRateLimited,
                "The plugin's server is rate limiting requests.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new McpProtocolException(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                $"The plugin's server answered {(int)response.StatusCode}.");
        }

        return ParseResult(ExtractJson(body), method);
    }

    /// <summary>
    /// Returns the JSON-RPC payload whether the server answered with a plain body or an SSE stream.
    /// </summary>
    private static string ExtractJson(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) return trimmed;

        // SSE: take the last data: line, which carries the response to the request just sent.
        var payload = body
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].Trim())
            .LastOrDefault(line => line.StartsWith('{'));

        return payload ?? trimmed;
    }

    private static JsonElement ParseResult(string json, string method)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new McpProtocolException(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                $"The plugin's server answered '{method}' with something that is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var text)
                    ? text.GetString()
                    : "no message";

                throw new McpProtocolException(
                    PluginConstants.ErrorCodes.ProviderUnavailable,
                    $"The plugin's server refused '{method}': {message}");
            }

            return root.TryGetProperty("result", out var result)
                ? result.Clone()
                : default;
        }
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
    private static McpToolDescriptorDto? ToDescriptor(string pluginKey, JsonElement tool)
    {
        var name = ReadString(tool, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var readOnly = tool.TryGetProperty("annotations", out var annotations)
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
            RequiredScopes: Array.Empty<string>(),
            Parameters: parameters);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
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

    /// <summary>Carries a plugin error code out of the transport without leaking HTTP upward.</summary>
    private sealed class McpProtocolException(string errorCode, string message) : Exception(message)
    {
        public string ErrorCode { get; } = errorCode;
    }
}
