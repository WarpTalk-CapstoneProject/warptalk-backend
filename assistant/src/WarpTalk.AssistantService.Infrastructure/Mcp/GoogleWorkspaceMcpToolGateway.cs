using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Web;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

public class GoogleWorkspaceMcpToolGateway : IMcpToolGateway
{
    private readonly HttpClient _httpClient;
    private readonly IPluginCredentialProtector _credentialProtector;
    private readonly GoogleWorkspaceApiOptions _options;

    /// <summary>
    /// Google's tools are authored by us in the catalog, not discovered: there is no official
    /// remote MCP server for Drive or Calendar to ask. So this echoes the catalog rather than
    /// calling out, and connecting a Google account never changes the tool set.
    /// </summary>
    public Task<IReadOnlyList<McpToolDescriptorDto>> ListToolsAsync(
        PluginDefinitionDto plugin,
        PluginConnection connection,
        CancellationToken ct = default) =>
        Task.FromResult(plugin.Tools);

    public GoogleWorkspaceMcpToolGateway(
        HttpClient httpClient,
        IPluginCredentialProtector credentialProtector,
        IOptions<GoogleWorkspaceApiOptions> options)
    {
        _httpClient = httpClient;
        _credentialProtector = credentialProtector;
        _options = options.Value;
    }

    public async Task<McpToolExecutionResult> ExecuteAsync(
        PluginDefinitionDto plugin,
        McpToolDescriptorDto tool,
        PluginConnection connection,
        McpToolExecutionRequest request,
        CancellationToken ct = default)
    {
        // Not dispatch - IPluginProviderResolver already picked this gateway from Plugin.Kind. This
        // is an invariant assertion, and it stays: the endpoints below come from Google-specific
        // options, so another provider routed here would have its user's token sent to Google.
        if (!string.Equals(plugin.Key, PluginConstants.GoogleWorkspace, StringComparison.Ordinal))
            return Failure(PluginConstants.ErrorCodes.UnknownPlugin, "Unsupported plugin.");

        if (string.IsNullOrWhiteSpace(connection.EncryptedAccessToken))
            return Failure(PluginConstants.ErrorCodes.ConnectionRequired, "Reconnect the provider account first.");

        var accessToken = _credentialProtector.Unprotect(connection.EncryptedAccessToken);
        return tool.Name switch
        {
            "google_drive_search" => await SearchDriveAsync(accessToken, request.Arguments, ct),
            "google_drive_get_file" => await GetDriveFileAsync(accessToken, request.Arguments, ct),
            "google_calendar_list_events" => await ListCalendarEventsAsync(accessToken, request.Arguments, ct),
            "google_calendar_create_event" => await CreateCalendarEventAsync(accessToken, request.Arguments, ct),
            _ => Failure(PluginConstants.ErrorCodes.UnknownTool, "Unsupported Google Workspace tool."),
        };
    }

    private async Task<McpToolExecutionResult> SearchDriveAsync(
        string accessToken,
        JsonObject? arguments,
        CancellationToken ct)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return Failure(PluginConstants.ErrorCodes.UnknownTool, "Drive search requires a query.");

        var limit = Math.Clamp(GetInt(arguments, "limit") ?? 10, 1, 20);
        var googleQuery = $"name contains '{EscapeDriveQuery(query)}' and trashed = false";
        var url = $"{_options.DriveFilesEndpoint}?pageSize={limit}&q={HttpUtility.UrlEncode(googleQuery)}&fields=files(id,name,mimeType,webViewLink,modifiedTime)";
        using var request = AuthorizedRequest(HttpMethod.Get, url, accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return await ProviderFailureAsync(response, ct);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? new JsonObject();
        var files = json["files"] as JsonArray ?? [];
        var data = new JsonObject { ["files"] = files.DeepClone() };
        return Success(data);
    }

    private async Task<McpToolExecutionResult> ListCalendarEventsAsync(
        string accessToken,
        JsonObject? arguments,
        CancellationToken ct)
    {
        var endpoint = CalendarEventsEndpoint("primary");
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["singleEvents"] = "true";
        query["orderBy"] = "startTime";
        var timeMin = GetString(arguments, "timeMin");
        var timeMax = GetString(arguments, "timeMax");
        if (!string.IsNullOrWhiteSpace(timeMin)) query["timeMin"] = timeMin;
        if (!string.IsNullOrWhiteSpace(timeMax)) query["timeMax"] = timeMax;

        using var request = AuthorizedRequest(HttpMethod.Get, $"{endpoint}?{query}", accessToken);
        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return await ProviderFailureAsync(response, ct);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? new JsonObject();
        var items = json["items"] as JsonArray ?? [];
        return Success(new JsonObject { ["events"] = items.DeepClone() });
    }

    private async Task<McpToolExecutionResult> GetDriveFileAsync(
        string accessToken,
        JsonObject? arguments,
        CancellationToken ct)
    {
        var fileId = GetString(arguments, "fileId");
        if (string.IsNullOrWhiteSpace(fileId))
            return Failure(PluginConstants.ErrorCodes.UnknownTool, "Reading a Drive file requires a fileId.");

        var metadataUrl = $"{_options.DriveFilesEndpoint}/{Uri.EscapeDataString(fileId)}?fields=id,name,mimeType,size,modifiedTime,webViewLink,description";
        using var metadataRequest = AuthorizedRequest(HttpMethod.Get, metadataUrl, accessToken);
        var metadataResponse = await _httpClient.SendAsync(metadataRequest, ct);
        if (!metadataResponse.IsSuccessStatusCode)
            return await ProviderFailureAsync(metadataResponse, ct);

        var metadataResponseJson = await metadataResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? new JsonObject();
        var metadata = SanitizeDriveMetadata(metadataResponseJson);
        var mimeType = metadataResponseJson["mimeType"]?.GetValue<string>();
        var size = GetLong(metadataResponseJson, "size");
        var data = new JsonObject { ["file"] = metadata };

        if (!IsSupportedTextFile(mimeType))
            return SuccessWithMessage(data, "unsupported", "This Drive file type is not supported for inline reading.");

        if (size.HasValue && size.Value > _options.MaxDriveFileBytes)
            return SuccessWithMessage(data, "too_large", "This Drive file is too large for inline reading.");

        var contentUrl = IsGoogleDocument(mimeType)
            ? $"{_options.DriveFilesEndpoint}/{Uri.EscapeDataString(fileId)}/export?mimeType=text/plain"
            : $"{_options.DriveFilesEndpoint}/{Uri.EscapeDataString(fileId)}?alt=media";
        using var contentRequest = AuthorizedRequest(HttpMethod.Get, contentUrl, accessToken);
        var contentResponse = await _httpClient.SendAsync(contentRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!contentResponse.IsSuccessStatusCode)
            return await ProviderFailureAsync(contentResponse, ct);

        var contentResult = await ReadBoundedTextAsync(contentResponse, ct);
        if (!contentResult.IsSuccess)
            return SuccessWithMessage(data, "too_large", "This Drive file is too large for inline reading.");

        data["content"] = contentResult.Content;
        data["contentStatus"] = "available";
        return Success(data);
    }

    private async Task<McpToolExecutionResult> CreateCalendarEventAsync(
        string accessToken,
        JsonObject? arguments,
        CancellationToken ct)
    {
        var summary = GetString(arguments, "summary");
        var start = GetString(arguments, "start");
        var end = GetString(arguments, "end");
        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
            return Failure(PluginConstants.ErrorCodes.UnknownTool, "Calendar event requires summary, start, and end.");

        var payload = new JsonObject
        {
            ["summary"] = summary,
            ["description"] = GetString(arguments, "description"),
            ["start"] = new JsonObject { ["dateTime"] = start },
            ["end"] = new JsonObject { ["dateTime"] = end },
        };

        using var request = AuthorizedRequest(HttpMethod.Post, CalendarEventsEndpoint("primary"), accessToken);
        request.Content = JsonContent.Create(payload);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return await ProviderFailureAsync(response, ct);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? new JsonObject();
        var eventId = json["id"]?.GetValue<string>();
        return Success(new JsonObject { ["event"] = json.DeepClone() }, eventId);
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<McpToolExecutionResult> ProviderFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var message = string.IsNullOrWhiteSpace(body)
            ? "Google Workspace provider request failed."
            : body;

        var providerReason = ExtractGoogleErrorReason(body);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Failure(PluginConstants.ErrorCodes.ConnectionRequired, "Reconnect the provider account first."),
            HttpStatusCode.Forbidden when IsInsufficientScopeForbidden(providerReason, body) => Failure(PluginConstants.ErrorCodes.MissingScope, "Reconnect the provider account with the required scopes."),
            HttpStatusCode.Forbidden => Failure(PluginConstants.ErrorCodes.ProviderUnavailable, ProviderRejectedMessage(providerReason)),
            (HttpStatusCode)429 => Failure(PluginConstants.ErrorCodes.ProviderRateLimited, "Google Workspace rate limit reached."),
            _ when (int)response.StatusCode >= 500 => Failure(PluginConstants.ErrorCodes.ProviderUnavailable, "Google Workspace is unavailable."),
            _ => Failure(PluginConstants.ErrorCodes.ProviderUnavailable, message),
        };
    }

    private static string ProviderRejectedMessage(string? providerReason)
    {
        return string.IsNullOrWhiteSpace(providerReason)
            ? "Google Workspace provider rejected the request."
            : $"Google Workspace provider rejected the request ({providerReason}).";
    }

    private static bool IsInsufficientScopeForbidden(string? providerReason, string body)
    {
        return string.Equals(providerReason, "insufficientPermissions", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerReason, "ACCESS_TOKEN_SCOPE_INSUFFICIENT", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("ACCESS_TOKEN_SCOPE_INSUFFICIENT", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("insufficientPermissions", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractGoogleErrorReason(string body)
    {
        try
        {
            var parsed = JsonNode.Parse(body);
            var reason = parsed?["error"]?["errors"]?.AsArray()
                .Select(error => error?["reason"]?.GetValue<string>())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(reason))
                return reason;

            return parsed?["error"]?["status"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string CalendarEventsEndpoint(string calendarId)
    {
        return string.Format(_options.CalendarEventsEndpointFormat, HttpUtility.UrlEncode(calendarId));
    }

    private static string EscapeDriveQuery(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static string? GetString(JsonObject? arguments, string name)
    {
        return arguments != null && arguments.TryGetPropertyValue(name, out var value)
            ? value?.GetValue<string>()
            : null;
    }

    private static int? GetInt(JsonObject? arguments, string name)
    {
        if (arguments == null || !arguments.TryGetPropertyValue(name, out var value) || value == null)
            return null;

        try
        {
            return value.GetValueKind() == JsonValueKind.Number
                ? value.GetValue<int>()
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static long? GetLong(JsonObject json, string name)
    {
        if (!json.TryGetPropertyValue(name, out var value) || value == null)
            return null;

        try
        {
            return value.GetValueKind() == JsonValueKind.Number ? value.GetValue<long>() : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsGoogleDocument(string? mimeType)
    {
        return string.Equals(mimeType, "application/vnd.google-apps.document", StringComparison.Ordinal);
    }

    private static bool IsSupportedTextFile(string? mimeType)
    {
        return !string.IsNullOrWhiteSpace(mimeType)
            && (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mimeType, "application/json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mimeType, "application/xml", StringComparison.OrdinalIgnoreCase)
                || IsGoogleDocument(mimeType));
    }

    private JsonObject SanitizeDriveMetadata(JsonObject source)
    {
        var metadata = new JsonObject();
        foreach (var name in new[] { "id", "name", "mimeType", "size", "modifiedTime", "webViewLink", "description" })
        {
            if (source.TryGetPropertyValue(name, out var value) && value != null)
                metadata[name] = value.DeepClone();
        }

        return metadata;
    }

    private async Task<(bool IsSuccess, string? Content)> ReadBoundedTextAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        var buffer = new char[4096];
        while (content.Length <= _options.MaxDriveFileCharacters)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                return (true, content.ToString());

            content.Append(buffer, 0, read);
        }

        return (false, null);
    }

    private static McpToolExecutionResult SuccessWithMessage(JsonObject data, string status, string message)
    {
        data["contentStatus"] = status;
        data["message"] = message;
        return Success(data);
    }

    private static McpToolExecutionResult Success(JsonObject data, string? providerResourceRef = null)
    {
        return new McpToolExecutionResult(true, null, null, data, providerResourceRef, null);
    }

    private static McpToolExecutionResult Failure(string errorCode, string message)
    {
        return new McpToolExecutionResult(false, errorCode, message, null, null, null);
    }
}
