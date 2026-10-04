using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<GoogleWorkspaceMcpToolGateway> _logger;

    // Kept even though Meet stores no title: the echoed summary is what the WarpBot card shows and
    // what the Calendar hop (which requires one) books under when the user named nothing.
    private const string DefaultMeetSummary = "Google Meet meeting";

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
        IOptions<GoogleWorkspaceApiOptions> options,
        ILogger<GoogleWorkspaceMcpToolGateway>? logger = null)
    {
        _httpClient = httpClient;
        _credentialProtector = credentialProtector;
        _options = options.Value;
        // Optional so tests can construct the gateway without a logging container.
        _logger = logger ?? NullLogger<GoogleWorkspaceMcpToolGateway>.Instance;
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
        //
        // Asserted on Provider, not on Key. There are three Google catalog rows since
        // 20260907100000 and there will be more; the thing that makes this gateway the right one
        // for all of them is that they talk to Google, which is exactly what Provider says.
        if (!string.Equals(plugin.Provider, PluginConstants.Providers.Google, StringComparison.Ordinal))
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
            "google_calendar_create_meet_event" => await CreateMeetEventAsync(accessToken, request.Arguments, ct),
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

        var description = GetString(arguments, "description");
        var timeZone = GetString(arguments, "timeZone");
        var meetLink = GetString(arguments, "meetLink");
        var hasMeetLink = !string.IsNullOrWhiteSpace(meetLink);
        var attendees = GetStringArray(arguments, "attendees");

        // Only an event that carries a Meet link (the Meet tool -> Calendar chain) gets the
        // workspace zone stamped on a bare date-time: the Meet tool echoes the model's times as it
        // got them, and Google refuses a date-time with neither offset nor zone. A plain calendar
        // event keeps exactly the payload it always had.
        if (hasMeetLink && string.IsNullOrWhiteSpace(timeZone) && (NeedsTimeZone(start) || NeedsTimeZone(end)))
            timeZone = _options.DefaultTimeZone;

        var payload = new JsonObject
        {
            ["summary"] = summary,
            ["description"] = description,
            ["start"] = CalendarEventDateTime(start, timeZone),
            ["end"] = CalendarEventDateTime(end, timeZone),
        };

        if (attendees.Count > 0)
        {
            payload["attendees"] = new JsonArray(attendees
                .Select(email => new JsonObject { ["email"] = email })
                .Cast<JsonNode?>()
                .ToArray());
        }

        // Invitations go out only when there is someone to invite; otherwise the query string
        // stays what it always was.
        var sendUpdates = attendees.Count > 0 ? "all" : null;
        HttpResponseMessage response;
        var conferenceAttached = false;
        if (hasMeetLink)
        {
            // The link also goes into location and description: those survive whatever Calendar
            // decides about a conference it did not create, so the event always carries the link.
            payload["location"] = meetLink;
            payload["description"] = AppendMeetLink(description, meetLink!);

            var withConference = (JsonObject)payload.DeepClone();
            withConference["conferenceData"] = ExistingMeetConferenceData(
                meetLink!,
                FirstNonBlank(GetString(arguments, "meetingCode"), MeetingCodeFromLink(meetLink)));

            response = await InsertCalendarEventAsync(accessToken, withConference, conferenceDataVersion: true, sendUpdates, ct);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // Calendar may refuse to attach a conference it did not create itself. The
                // location/description copy is enough for the user to join, so retry once without it
                // rather than failing a booking over the nicer "Join with Google Meet" button.
                _logger.LogWarning(
                    "Calendar rejected conferenceData for an existing Meet link; retrying with the link in location/description only.");
                response.Dispose();
                response = await InsertCalendarEventAsync(accessToken, payload, conferenceDataVersion: false, sendUpdates, ct);
            }
            else if (response.IsSuccessStatusCode)
            {
                conferenceAttached = true;
            }
        }
        else
        {
            response = await InsertCalendarEventAsync(accessToken, payload, conferenceDataVersion: false, sendUpdates, ct);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return await ProviderFailureAsync(response, ct);

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
                ?? new JsonObject();
            var eventId = json["id"]?.GetValue<string>();
            // "Attached" means Calendar kept it, not merely that it accepted the request.
            conferenceAttached = conferenceAttached && json["conferenceData"] != null;
            if (hasMeetLink)
            {
                _logger.LogInformation(
                    "Calendar event {EventId} created with existing Meet link via {Path}.",
                    eventId,
                    conferenceAttached ? "conferenceData" : "location/description");
            }

            var data = new JsonObject
            {
                ["provider"] = "google_calendar",
                ["eventId"] = eventId,
                ["htmlLink"] = json["htmlLink"]?.DeepClone(),
                ["hangoutLink"] = json["hangoutLink"]?.DeepClone(),
                ["meetLink"] = hasMeetLink ? meetLink : null,
                ["summary"] = json["summary"]?.DeepClone() ?? summary,
                ["start"] = EventTime(json["start"]) ?? start,
                ["end"] = EventTime(json["end"]) ?? end,
                ["conferenceAttached"] = conferenceAttached,
                ["event"] = json.DeepClone(),
            };
            return Success(data, eventId);
        }
    }

    private async Task<HttpResponseMessage> InsertCalendarEventAsync(
        string accessToken,
        JsonObject payload,
        bool conferenceDataVersion,
        string? sendUpdates,
        CancellationToken ct)
    {
        var url = CalendarEventsEndpoint("primary");
        if (conferenceDataVersion)
            url = AppendQuery(url, "conferenceDataVersion=1");
        if (!string.IsNullOrWhiteSpace(sendUpdates))
            url = AppendQuery(url, $"sendUpdates={sendUpdates}");

        using var request = AuthorizedRequest(HttpMethod.Post, url, accessToken);
        request.Content = JsonContent.Create(payload);
        return await _httpClient.SendAsync(request, ct);
    }

    /// <summary>
    /// A conference that already exists (the Meet tool made it through the Meet API). Calendar
    /// attaches it by id + entry point instead of a createRequest, so nothing new is created.
    /// </summary>
    private static JsonObject ExistingMeetConferenceData(string meetLink, string? meetingCode)
    {
        var conferenceData = new JsonObject
        {
            ["conferenceSolution"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "hangoutsMeet" },
            },
            ["entryPoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["entryPointType"] = "video",
                    ["uri"] = meetLink,
                    ["label"] = StripScheme(meetLink),
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(meetingCode))
            conferenceData["conferenceId"] = meetingCode;

        return conferenceData;
    }

    private static string AppendMeetLink(string? description, string meetLink)
    {
        if (string.IsNullOrWhiteSpace(description))
            return $"Join with Google Meet: {meetLink}";

        return description.Contains(meetLink, StringComparison.Ordinal)
            ? description
            : $"{description}\n\nJoin with Google Meet: {meetLink}";
    }

    private static string StripScheme(string uri)
    {
        var separator = uri.IndexOf("://", StringComparison.Ordinal);
        return separator < 0 ? uri : uri[(separator + 3)..];
    }

    private static string? EventTime(JsonNode? time)
    {
        return time?["dateTime"]?.GetValue<string>() ?? time?["date"]?.GetValue<string>();
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string AppendQuery(string url, string pair)
    {
        return url.Contains('?', StringComparison.Ordinal) ? $"{url}&{pair}" : $"{url}?{pair}";
    }

    /// <summary>
    /// The google_meet plugin's one tool. It creates a Meet space through the Meet REST API
    /// (scope meetings.space.created) and nothing else: Meet stores no title or time, so connecting
    /// only Meet never writes to the user's Calendar. The tool keeps its historical key
    /// google_calendar_create_meet_event because tool policies and prompts are keyed on it.
    ///
    /// The optional summary/start/end/timeZone/description/attendees are echoed back untouched so
    /// the AI worker can hand them to google_calendar_create_event when the user also wants the
    /// meeting on a calendar. start/end are no longer defaulted to "now + 30 minutes": that
    /// default only existed because a Calendar event needs times, and a Meet space does not.
    /// </summary>
    private async Task<McpToolExecutionResult> CreateMeetEventAsync(
        string accessToken,
        JsonObject? arguments,
        CancellationToken ct)
    {
        var summary = GetString(arguments, "summary");
        if (string.IsNullOrWhiteSpace(summary))
            summary = DefaultMeetSummary;

        var start = NullIfBlank(GetString(arguments, "start"));
        var end = NullIfBlank(GetString(arguments, "end"));

        // Echoed so the Calendar hop books the moment the confirmation card printed: a bare
        // "2026-09-24T10:00:00" means the workspace zone, same rule the Calendar call applies.
        var timeZone = NullIfBlank(GetString(arguments, "timeZone"));
        if (timeZone == null && (NeedsTimeZone(start) || NeedsTimeZone(end)))
            timeZone = _options.DefaultTimeZone;

        using var request = AuthorizedRequest(HttpMethod.Post, _options.MeetSpacesEndpoint, accessToken);
        // An empty Space: access type and the rest follow the user's / organisation's Meet
        // defaults, which is what a meeting made in the Meet UI would get.
        request.Content = JsonContent.Create(new JsonObject());

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return await ProviderFailureAsync(response, ct);

        var space = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? new JsonObject();
        var spaceName = space["name"]?.GetValue<string>();
        var meetLink = NullIfBlank(space["meetingUri"]?.GetValue<string>());
        var meetingCode = FirstNonBlank(space["meetingCode"]?.GetValue<string>(), MeetingCodeFromLink(meetLink));

        var attendees = GetStringArray(arguments, "attendees");
        var data = new JsonObject
        {
            ["provider"] = "google_meet",
            ["spaceName"] = spaceName,
            ["meetLink"] = meetLink,
            ["meetingCode"] = meetingCode,
            // spaces.create answers synchronously; there is no pending state to wait out any more.
            ["meetLinkStatus"] = meetLink == null ? "failure" : "success",
            ["summary"] = summary,
            ["start"] = start,
            ["end"] = end,
            ["timeZone"] = timeZone,
            ["description"] = NullIfBlank(GetString(arguments, "description")),
            ["attendees"] = new JsonArray(attendees.Select(email => (JsonNode?)JsonValue.Create(email)).ToArray()),
        };

        return Success(data, spaceName);
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? MeetingCodeFromLink(string? meetLink)
    {
        if (string.IsNullOrWhiteSpace(meetLink) || !Uri.TryCreate(meetLink, UriKind.Absolute, out var uri))
            return null;

        // https://meet.google.com/abc-defg-hij?authuser=0 -> abc-defg-hij
        var code = uri.AbsolutePath.Trim('/');
        return string.IsNullOrWhiteSpace(code) ? null : code;
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

    /// <summary>
    /// Whether Google would refuse this date-time for having no zone: no trailing offset and no
    /// "Z". "2026-09-24T10:00:00" is what a model sends when it was told the local date.
    /// </summary>
    private static bool NeedsTimeZone(string? dateTime)
    {
        if (string.IsNullOrWhiteSpace(dateTime))
            return false;

        return !DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            || (DateTime.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var local)
                && local.Kind == DateTimeKind.Unspecified);
    }

    private static JsonObject CalendarEventDateTime(string dateTime, string? timeZone)
    {
        var value = new JsonObject { ["dateTime"] = dateTime };
        if (!string.IsNullOrWhiteSpace(timeZone))
            value["timeZone"] = timeZone;

        return value;
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

    private static IReadOnlyList<string> GetStringArray(JsonObject? arguments, string name)
    {
        if (arguments == null || !arguments.TryGetPropertyValue(name, out var value) || value is not JsonArray values)
            return [];

        return values
            .Select(item => item?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
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
