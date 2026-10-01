using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Infrastructure.Meet;

namespace WarpTalk.AssistantService.Tests.Meet;

public class GoogleMeetRestClientTests
{
    private readonly List<HttpRequestMessage> _requests = [];

    private GoogleMeetRestClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler, int pageSize = 100) =>
        new(
            new HttpClient(new FakeHandler(request =>
            {
                _requests.Add(request);
                return handler(request);
            })),
            Options.Create(new GoogleMeetApiOptions { BaseUrl = "https://meet.test/v2/", PageSize = pageSize }));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task FindConferenceRecords_FiltersByMeetingCode_AndSendsTheBearerToken()
    {
        var client = Client(_ => Json("""
            { "conferenceRecords": [
                { "name": "conferenceRecords/rec-1", "startTime": "2026-10-01T09:00:00Z", "endTime": "2026-10-01T10:00:00.5Z", "space": "spaces/x" },
                { "name": "conferenceRecords/rec-2", "startTime": "2026-10-01T11:00:00Z" }
            ] }
            """));

        var result = await client.FindConferenceRecordsAsync("token-1", "abc-mnop-xyz");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, 500, TimeSpan.Zero), result.Value[0].EndTime);
        Assert.Null(result.Value[1].EndTime);

        var request = Assert.Single(_requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("token-1", request.Headers.Authorization.Parameter);
        Assert.Equal("/v2/conferenceRecords", request.RequestUri!.AbsolutePath);
        Assert.Contains("filter=space.meeting_code%3D%22abc-mnop-xyz%22", request.RequestUri.Query);
    }

    [Fact]
    public async Task FindConferenceRecords_RefusesAnUnnormalizedCode_WithoutCallingGoogle()
    {
        var client = Client(_ => Json("{}"));

        var result = await client.FindConferenceRecordsAsync("t", "abc\" OR 1=1");

        Assert.Equal(MeetRestErrorKind.InvalidRequest, result.Error);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task ListParticipants_FollowsPages_AndReadsEveryParticipantKind()
    {
        var client = Client(request => request.RequestUri!.Query.Contains("pageToken=p2", StringComparison.Ordinal)
            ? Json("""
                { "participants": [
                    { "name": "conferenceRecords/rec-1/participants/3", "phoneUser": { "displayName": "+84 *** 123" }, "earliestStartTime": "2026-10-01T09:05:00Z" }
                ] }
                """)
            : Json("""
                { "participants": [
                    { "name": "conferenceRecords/rec-1/participants/1", "signedinUser": { "user": "users/42", "displayName": "Alice Nguyen" }, "earliestStartTime": "2026-10-01T09:00:00Z", "latestEndTime": "2026-10-01T09:50:00Z" },
                    { "name": "conferenceRecords/rec-1/participants/2", "anonymousUser": { "displayName": "Guest Bob" }, "earliestStartTime": "2026-10-01T09:01:00Z" }
                  ],
                  "nextPageToken": "p2" }
                """), pageSize: 2);

        var result = await client.ListParticipantsAsync("t", "conferenceRecords/rec-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _requests.Count);
        Assert.Contains("pageSize=2", _requests[0].RequestUri!.Query);
        Assert.Collection(
            result.Value!,
            p =>
            {
                Assert.Equal("conferenceRecords/rec-1/participants/1", p.Key);
                Assert.Equal("Alice Nguyen", p.DisplayName);
                Assert.Equal(MeetParticipantKinds.SignedIn, p.Kind);
                Assert.NotNull(p.LeftAt);
            },
            p =>
            {
                Assert.Equal("Guest Bob", p.DisplayName);
                Assert.Equal(MeetParticipantKinds.Anonymous, p.Kind);
                Assert.Null(p.LeftAt);
            },
            p => Assert.Equal(MeetParticipantKinds.Phone, p.Kind));
    }

    [Fact]
    public async Task ListTranscriptsAndEntries_ReadStatesAndEntries()
    {
        var client = Client(request => request.RequestUri!.AbsolutePath.EndsWith("/entries", StringComparison.Ordinal)
            ? Json("""
                { "transcriptEntries": [
                    { "name": "conferenceRecords/rec-1/transcripts/t1/entries/e1", "participant": "conferenceRecords/rec-1/participants/1",
                      "text": "Hello everyone", "languageCode": "en-US", "startTime": "2026-10-01T09:00:01Z", "endTime": "2026-10-01T09:00:03Z" }
                ] }
                """)
            : Json("""{ "transcripts": [ { "name": "conferenceRecords/rec-1/transcripts/t1", "state": "FILE_GENERATED" }, { "name": "conferenceRecords/rec-1/transcripts/t2", "state": "STARTED" } ] }"""));

        var transcripts = await client.ListTranscriptsAsync("t", "conferenceRecords/rec-1");
        var entries = await client.ListTranscriptEntriesAsync("t", "conferenceRecords/rec-1/transcripts/t1");

        Assert.Equal(new[] { true, false }, transcripts.Value!.Select(t => t.IsReadable));
        var entry = Assert.Single(entries.Value!);
        Assert.Equal("conferenceRecords/rec-1/participants/1", entry.Participant);
        Assert.Equal("Hello everyone", entry.Text);
        Assert.Equal("en-US", entry.LanguageCode);
        Assert.Equal("/v2/conferenceRecords/rec-1/transcripts/t1/entries", _requests[1].RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("conferenceRecords/../spaces/x")]
    [InlineData("conferenceRecords/rec-1?x=1")]
    [InlineData("spaces/abc")]
    public async Task ResourceNamesThatAreNotConferenceRecords_AreRefused(string name)
    {
        var client = Client(_ => Json("{}"));

        var result = await client.ListParticipantsAsync("t", name);

        Assert.Equal(MeetRestErrorKind.InvalidRequest, result.Error);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", MeetRestErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":403,"status":"PERMISSION_DENIED","details":[{"reason":"ACCESS_TOKEN_SCOPE_INSUFFICIENT"}]}}""", MeetRestErrorKind.ScopeMissing)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"message":"Request had insufficient authentication scopes."}}""", MeetRestErrorKind.ScopeMissing)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"status":"PERMISSION_DENIED"}}""", MeetRestErrorKind.PermissionDenied)]
    [InlineData(HttpStatusCode.NotFound, "{}", MeetRestErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", MeetRestErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, "", MeetRestErrorKind.Unavailable)]
    public async Task ProviderErrors_AreTyped(HttpStatusCode status, string body, MeetRestErrorKind expected)
    {
        var client = Client(_ => Json(body, status));

        var result = await client.FindConferenceRecordsAsync("t", "abc-mnop-xyz");

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task NetworkFailure_IsUnavailable_NotAnException()
    {
        var client = Client(_ => throw new HttpRequestException("connection reset"));

        var result = await client.ListTranscriptsAsync("t", "conferenceRecords/rec-1");

        Assert.Equal(MeetRestErrorKind.Unavailable, result.Error);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
