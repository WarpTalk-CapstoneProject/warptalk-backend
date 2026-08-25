using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        if (!string.Equals(plugin.Key, PluginConstants.GoogleWorkspace, StringComparison.Ordinal))
            return Failure(PluginConstants.ErrorCodes.UnknownPlugin, "Unsupported plugin.");

        if (string.IsNullOrWhiteSpace(connection.EncryptedAccessToken))
            return Failure(PluginConstants.ErrorCodes.ConnectionRequired, "Reconnect the provider account first.");

        var accessToken = _credentialProtector.Unprotect(connection.EncryptedAccessToken);
        return tool.Name switch
        {
            "google_drive_search" => await SearchDriveAsync(accessToken, request.Arguments, ct),
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

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Failure(PluginConstants.ErrorCodes.ConnectionRequired, "Reconnect the provider account first."),
            HttpStatusCode.Forbidden => Failure(PluginConstants.ErrorCodes.MissingScope, "Reconnect the provider account with the required scopes."),
            (HttpStatusCode)429 => Failure(PluginConstants.ErrorCodes.ProviderRateLimited, "Google Workspace rate limit reached."),
            _ when (int)response.StatusCode >= 500 => Failure(PluginConstants.ErrorCodes.ProviderUnavailable, "Google Workspace is unavailable."),
            _ => Failure(PluginConstants.ErrorCodes.ProviderUnavailable, message),
        };
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

    private static McpToolExecutionResult Success(JsonObject data, string? providerResourceRef = null)
    {
        return new McpToolExecutionResult(true, null, null, data, providerResourceRef, null);
    }

    private static McpToolExecutionResult Failure(string errorCode, string message)
    {
        return new McpToolExecutionResult(false, errorCode, message, null, null, null);
    }
}
