using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.Infrastructure.Meet;

public class GoogleMeetApiOptions
{
    /// <summary>Meet REST v2 root, with the trailing slash.</summary>
    public string BaseUrl { get; set; } = "https://meet.googleapis.com/v2/";

    /// <summary>Page size asked for on every list. Google caps it per collection anyway.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Hard stop on pagination, so a provider bug cannot loop us forever.</summary>
    public int MaxPages { get; set; } = 50;
}

/// <inheritdoc cref="IGoogleMeetRestClient"/>
public sealed partial class GoogleMeetRestClient : IGoogleMeetRestClient
{
    private readonly HttpClient _httpClient;
    private readonly GoogleMeetApiOptions _options;

    public GoogleMeetRestClient(HttpClient httpClient, IOptions<GoogleMeetApiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    // Resource names come back from Google and are put straight into a path. Only the shapes this
    // client actually reads are accepted, so nothing else — a "..", a query string, another
    // collection — can ride along on a name.
    [GeneratedRegex("^conferenceRecords/[A-Za-z0-9_-]+$")]
    private static partial Regex ConferenceRecordName();

    [GeneratedRegex("^conferenceRecords/[A-Za-z0-9_-]+/transcripts/[A-Za-z0-9_-]+$")]
    private static partial Regex TranscriptName();

    [GeneratedRegex("^[a-z]{3,4}-[a-z]{3,4}-[a-z]{3,4}$")]
    private static partial Regex MeetCode();

    public async Task<MeetRestResult<IReadOnlyList<MeetConferenceRecordDto>>> FindConferenceRecordsAsync(
        string accessToken, string meetCode, CancellationToken ct = default)
    {
        if (!MeetCode().IsMatch(meetCode))
            return MeetRestResult<IReadOnlyList<MeetConferenceRecordDto>>.Failure(MeetRestErrorKind.InvalidRequest, "Not a normalized Meet code.");

        var filter = Uri.EscapeDataString($"space.meeting_code=\"{meetCode}\"");
        return await ListAsync(accessToken, $"conferenceRecords?filter={filter}", "conferenceRecords", ParseRecord, ct);
    }

    public async Task<MeetRestResult<IReadOnlyList<MeetParticipantDto>>> ListParticipantsAsync(
        string accessToken, string conferenceRecord, CancellationToken ct = default)
    {
        if (!ConferenceRecordName().IsMatch(conferenceRecord))
            return MeetRestResult<IReadOnlyList<MeetParticipantDto>>.Failure(MeetRestErrorKind.InvalidRequest, "Not a conference record name.");

        return await ListAsync(accessToken, $"{conferenceRecord}/participants", "participants", ParseParticipant, ct);
    }

    public async Task<MeetRestResult<IReadOnlyList<MeetTranscriptDto>>> ListTranscriptsAsync(
        string accessToken, string conferenceRecord, CancellationToken ct = default)
    {
        if (!ConferenceRecordName().IsMatch(conferenceRecord))
            return MeetRestResult<IReadOnlyList<MeetTranscriptDto>>.Failure(MeetRestErrorKind.InvalidRequest, "Not a conference record name.");

        return await ListAsync(accessToken, $"{conferenceRecord}/transcripts", "transcripts", ParseTranscript, ct);
    }

    public async Task<MeetRestResult<IReadOnlyList<MeetTranscriptEntryDto>>> ListTranscriptEntriesAsync(
        string accessToken, string transcript, CancellationToken ct = default)
    {
        if (!TranscriptName().IsMatch(transcript))
            return MeetRestResult<IReadOnlyList<MeetTranscriptEntryDto>>.Failure(MeetRestErrorKind.InvalidRequest, "Not a transcript name.");

        return await ListAsync(accessToken, $"{transcript}/entries", "transcriptEntries", ParseEntry, ct);
    }

    private async Task<MeetRestResult<IReadOnlyList<T>>> ListAsync<T>(
        string accessToken,
        string relativePath,
        string collection,
        Func<JsonObject, T?> parse,
        CancellationToken ct)
        where T : class
    {
        var items = new List<T>();
        string? pageToken = null;
        for (var page = 0; page < _options.MaxPages; page++)
        {
            var separator = relativePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var url = $"{BaseUrl()}{relativePath}{separator}pageSize={_options.PageSize.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrEmpty(pageToken))
                url += "&pageToken=" + Uri.EscapeDataString(pageToken);

            JsonObject? json;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    return MeetRestResult<IReadOnlyList<T>>.Failure(Classify(response.StatusCode, body), Truncate(body));
                }

                var text = await response.Content.ReadAsStringAsync(ct);
                json = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                return MeetRestResult<IReadOnlyList<T>>.Failure(MeetRestErrorKind.Unavailable, ex.Message);
            }

            if (json is null)
                return MeetRestResult<IReadOnlyList<T>>.Failure(MeetRestErrorKind.Unavailable, "Unreadable response.");

            if (json[collection] is JsonArray array)
            {
                foreach (var node in array.OfType<JsonObject>())
                {
                    var item = parse(node);
                    if (item is not null) items.Add(item);
                }
            }

            pageToken = json["nextPageToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pageToken))
                return MeetRestResult<IReadOnlyList<T>>.Success(items);
        }

        // More pages than any meeting has: return what was read rather than nothing.
        return MeetRestResult<IReadOnlyList<T>>.Success(items);
    }

    public static MeetRestErrorKind Classify(HttpStatusCode status, string body) =>
        status switch
        {
            HttpStatusCode.Unauthorized => MeetRestErrorKind.Unauthorized,
            HttpStatusCode.Forbidden when IsInsufficientScope(body) => MeetRestErrorKind.ScopeMissing,
            HttpStatusCode.Forbidden => MeetRestErrorKind.PermissionDenied,
            HttpStatusCode.NotFound => MeetRestErrorKind.NotFound,
            HttpStatusCode.TooManyRequests => MeetRestErrorKind.RateLimited,
            HttpStatusCode.BadRequest => MeetRestErrorKind.InvalidRequest,
            _ => MeetRestErrorKind.Unavailable,
        };

    private static bool IsInsufficientScope(string body) =>
        body.Contains("ACCESS_TOKEN_SCOPE_INSUFFICIENT", StringComparison.OrdinalIgnoreCase)
        || body.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase)
        || body.Contains("insufficientPermissions", StringComparison.OrdinalIgnoreCase);

    private string BaseUrl() => _options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/";

    private static string Truncate(string body) => body.Length <= 500 ? body : body[..500];

    private static MeetConferenceRecordDto? ParseRecord(JsonObject json)
    {
        var name = String(json, "name");
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new MeetConferenceRecordDto(name, Time(json, "startTime"), Time(json, "endTime"));
    }

    private static MeetParticipantDto? ParseParticipant(JsonObject json)
    {
        var name = String(json, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var (kind, user) = json["signedinUser"] is JsonObject signedIn ? (MeetParticipantKinds.SignedIn, signedIn)
            : json["anonymousUser"] is JsonObject anonymous ? (MeetParticipantKinds.Anonymous, anonymous)
            : json["phoneUser"] is JsonObject phone ? (MeetParticipantKinds.Phone, phone)
            : (MeetParticipantKinds.Anonymous, null);

        var displayName = (user is null ? null : String(user, "displayName")) ?? String(json, "displayName") ?? string.Empty;
        return new MeetParticipantDto(name, displayName, kind, Time(json, "earliestStartTime"), Time(json, "latestEndTime"));
    }

    private static MeetTranscriptDto? ParseTranscript(JsonObject json)
    {
        var name = String(json, "name");
        return string.IsNullOrWhiteSpace(name) ? null : new MeetTranscriptDto(name, String(json, "state") ?? string.Empty);
    }

    private static MeetTranscriptEntryDto? ParseEntry(JsonObject json)
    {
        var participant = String(json, "participant");
        if (string.IsNullOrWhiteSpace(participant)) return null;

        return new MeetTranscriptEntryDto(
            String(json, "name") ?? string.Empty,
            participant,
            String(json, "text") ?? string.Empty,
            String(json, "languageCode"),
            Time(json, "startTime"),
            Time(json, "endTime"));
    }

    private static string? String(JsonObject json, string property) =>
        json[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DateTimeOffset? Time(JsonObject json, string property)
    {
        var text = String(json, property);
        return text is not null
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
    }
}
