using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Infrastructure.Mcp;

namespace WarpTalk.AssistantService.Tests.Plugins;

public class GoogleWorkspaceMcpToolGatewayTests
{
    // Post-split key. What makes this gateway the right one for a row is its provider, not its
    // key, so these fixtures deliberately no longer name the retired google_workspace row.
    private const string GoogleDriveKey = "google_drive";

    [Fact]
    public async Task ExecuteAsync_SearchDrive_UsesPersonalAccessTokenAndReturnsFiles()
    {
        HttpRequestMessage? capturedRequest = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["files"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "file-1",
                            ["name"] = "Roadmap",
                            ["webViewLink"] = "https://drive.google.test/file-1",
                        },
                    },
                }),
            };
        }));
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        var sut = new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions
            {
                DriveFilesEndpoint = "https://google.test/drive/v3/files",
            }));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            DriveSearchTool(),
            new PluginConnection
            {
                UserId = Guid.NewGuid(),
                PluginId = Guid.NewGuid(),
                Status = PluginConstants.ConnectionStatus.Connected,
                EncryptedAccessToken = "encrypted-access-token",
            },
            new McpToolExecutionRequest(
                Guid.NewGuid(),
                GoogleDriveKey,
                "google_drive_search",
                new JsonObject { ["query"] = "roadmap", ["limit"] = 5 },
                null,
                null,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("plain-access-token", capturedRequest.Headers.Authorization.Parameter);
        Assert.Contains("name+contains", capturedRequest.RequestUri!.Query);
        var files = result.Data!["files"]!.AsArray();
        Assert.Equal("file-1", files[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_MapsUnauthorizedToConnectionRequired()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        var sut = new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions
            {
                DriveFilesEndpoint = "https://google.test/drive/v3/files",
            }));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            DriveSearchTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                GoogleDriveKey,
                "google_drive_search",
                new JsonObject { ["query"] = "roadmap" },
                null,
                null,
                null));

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_MapsForbiddenInsufficientScopeToMissingScope()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["errors"] = new JsonArray
                        {
                            new JsonObject { ["reason"] = "insufficientPermissions" },
                        },
                    },
                }),
            }));
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        var sut = new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions
            {
                DriveFilesEndpoint = "https://google.test/drive/v3/files",
            }));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            DriveSearchTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                GoogleDriveKey,
                "google_drive_search",
                new JsonObject { ["query"] = "roadmap" },
                null,
                null,
                null));

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.MissingScope, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_MapsForbiddenProviderConfigurationFailureToProviderUnavailable()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["errors"] = new JsonArray
                        {
                            new JsonObject { ["reason"] = "accessNotConfigured" },
                        },
                    },
                }),
            }));
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        var sut = new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions
            {
                DriveFilesEndpoint = "https://google.test/drive/v3/files",
            }));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            DriveSearchTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                GoogleDriveKey,
                "google_drive_search",
                new JsonObject { ["query"] = "roadmap" },
                null,
                null,
                null));

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.ErrorCode);
        Assert.Equal("Google Workspace provider rejected the request (accessNotConfigured).", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_GetDriveFile_ExportsGoogleDocAndReturnsSanitizedBoundedContent()
    {
        var requests = new List<HttpRequestMessage>();
        var responses = new Queue<HttpResponseMessage>([
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "doc-1",
                    ["name"] = "Roadmap",
                    ["mimeType"] = "application/vnd.google-apps.document",
                    ["modifiedTime"] = "2026-08-25T10:00:00Z",
                    ["webViewLink"] = "https://drive.google.test/doc-1",
                    ["owner"] = new JsonObject { ["emailAddress"] = "private@example.com" },
                }),
            },
            new(HttpStatusCode.OK) { Content = new StringContent("bounded roadmap text") },
        ]);
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return responses.Dequeue();
        }));
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        var sut = new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions
            {
                DriveFilesEndpoint = "https://google.test/drive/v3/files",
            }));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            DriveGetFileTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                GoogleDriveKey,
                "google_drive_get_file",
                new JsonObject { ["fileId"] = "doc-1" },
                null,
                null,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, requests.Count);
        Assert.Contains("/doc-1/export", requests[1].RequestUri!.AbsoluteUri);
        Assert.Equal("available", result.Data!["contentStatus"]!.GetValue<string>());
        Assert.Equal("bounded roadmap text", result.Data["content"]!.GetValue<string>());
        Assert.Null(result.Data["file"]!["owner"]);
    }

    [Fact]
    public async Task ExecuteAsync_GetDriveFile_RejectsUnsupportedFileWithoutFetchingBytes()
    {
        var requestCount = 0;
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "binary-1",
                    ["name"] = "Photo",
                    ["mimeType"] = "image/png",
                    ["size"] = 100,
                }),
            };
        }));
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        var sut = new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions { DriveFilesEndpoint = "https://google.test/drive/v3/files" }));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            DriveGetFileTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                GoogleDriveKey,
                "google_drive_get_file",
                new JsonObject { ["fileId"] = "binary-1" },
                null,
                null,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal("unsupported", result.Data!["contentStatus"]!.GetValue<string>());
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ExecuteAsync_GetDriveFile_RejectsMissingFileIdBeforeProviderCall()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Provider must not be called.")));
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        var sut = new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions()));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            DriveGetFileTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                GoogleDriveKey,
                "google_drive_get_file",
                new JsonObject(),
                null,
                null,
                null));

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.UnknownTool, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_GetDriveFile_RejectsOversizedTextBeforeFetchingContent()
    {
        var requestCount = 0;
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "text-1",
                    ["name"] = "Large text",
                    ["mimeType"] = "text/plain",
                    ["size"] = 201,
                }),
            };
        }));
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        var sut = new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions
            {
                DriveFilesEndpoint = "https://google.test/drive/v3/files",
                MaxDriveFileBytes = 200,
            }));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            DriveGetFileTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                GoogleDriveKey,
                "google_drive_get_file",
                new JsonObject { ["fileId"] = "text-1" },
                null,
                null,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal("too_large", result.Data!["contentStatus"]!.GetValue<string>());
        Assert.Equal(1, requestCount);
    }

    // ---- GMCAL1001: the Meet tool calls the Meet API only --------------------------------------

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_CreatesAMeetSpaceAndNeverCallsCalendar()
    {
        var requests = new List<(HttpRequestMessage Request, string Body)>();
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add((request, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["name"] = "spaces/jQCFfuBOdN5z",
                    ["meetingUri"] = "https://meet.google.com/abc-mnop-xyz",
                    ["meetingCode"] = "abc-mnop-xyz",
                    ["config"] = new JsonObject { ["accessType"] = "TRUSTED" },
                }),
            };
        }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject
        {
            ["summary"] = "Customer sync",
            ["start"] = "2026-09-05T15:00:00+07:00",
            ["end"] = "2026-09-05T15:30:00+07:00",
            ["timeZone"] = "Asia/Bangkok",
            ["description"] = "Quarterly customer sync",
            ["attendees"] = new JsonArray("nhi@example.com", "tu@example.com"),
        });

        Assert.True(result.IsSuccess);
        var (request, body) = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://google.test/meet/v2/spaces", request.RequestUri!.ToString());
        Assert.Equal("plain-access-token", request.Headers.Authorization!.Parameter);
        Assert.Equal("{}", body);
        Assert.DoesNotContain(requests, r => r.Request.RequestUri!.AbsolutePath.Contains("calendar", StringComparison.OrdinalIgnoreCase));

        var data = result.Data!;
        Assert.Equal("spaces/jQCFfuBOdN5z", result.ProviderResourceRef);
        Assert.Equal("google_meet", data["provider"]!.GetValue<string>());
        Assert.Equal("spaces/jQCFfuBOdN5z", data["spaceName"]!.GetValue<string>());
        Assert.Equal("https://meet.google.com/abc-mnop-xyz", data["meetLink"]!.GetValue<string>());
        Assert.Equal("abc-mnop-xyz", data["meetingCode"]!.GetValue<string>());
        Assert.Equal("success", data["meetLinkStatus"]!.GetValue<string>());
        // Echoed for the Calendar hop; Meet itself stored none of it.
        Assert.Equal("Customer sync", data["summary"]!.GetValue<string>());
        Assert.Equal("2026-09-05T15:00:00+07:00", data["start"]!.GetValue<string>());
        Assert.Equal("2026-09-05T15:30:00+07:00", data["end"]!.GetValue<string>());
        Assert.Equal("Asia/Bangkok", data["timeZone"]!.GetValue<string>());
        Assert.Equal("Quarterly customer sync", data["description"]!.GetValue<string>());
        Assert.Equal(["nhi@example.com", "tu@example.com"], data["attendees"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Null(data["eventId"]);
        Assert.Null(data["calendarEventLink"]);
        Assert.Null(data["event"]);
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_LeavesTimesNullWhenNoneWereGiven()
    {
        // Meet stores no time, so there is nothing to default any more: "now + 30 minutes" was a
        // Calendar requirement and would put a made-up slot on the confirmation card.
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["name"] = "spaces/now",
                    ["meetingUri"] = "https://meet.google.com/abc-defg-hij",
                }),
            }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject());

        Assert.True(result.IsSuccess);
        var data = result.Data!;
        Assert.Equal("Google Meet meeting", data["summary"]!.GetValue<string>());
        Assert.Null(data["start"]);
        Assert.Null(data["end"]);
        Assert.Null(data["timeZone"]);
        Assert.Empty(data["attendees"]!.AsArray());
        // No meetingCode in the response: parsed from the link.
        Assert.Equal("abc-defg-hij", data["meetingCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_EchoesTheWorkspaceZoneForATimeThatCarriesNone()
    {
        // The Calendar hop books whatever zone is echoed here, and Google refuses a bare
        // date-time. The workspace zone is the one WarpBot's confirmation card prints.
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["name"] = "spaces/bare",
                    ["meetingUri"] = "https://meet.google.com/abc-defg-hij",
                    ["meetingCode"] = "abc-defg-hij",
                }),
            }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject
        {
            ["start"] = "2026-09-24T10:00:00",
            ["end"] = "2026-09-24T10:30:00",
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("2026-09-24T10:00:00", result.Data!["start"]!.GetValue<string>());
        Assert.Equal("Asia/Ho_Chi_Minh", result.Data["timeZone"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_MapsMissingMeetScopeToMissingScope()
    {
        // A Meet grant from before GMCAL1001 carries calendar.events, not meetings.space.created.
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"error":{"code":403,"message":"Request had insufficient authentication scopes.","status":"PERMISSION_DENIED","details":[{"reason":"ACCESS_TOKEN_SCOPE_INSUFFICIENT"}]}}"""),
            }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject());

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.MissingScope, result.ErrorCode);
    }

    // ---- GMCAL1001: Calendar create_event can carry an existing Meet link -------------------------

    [Fact]
    public async Task ExecuteAsync_CreateCalendarEvent_WithoutMeetLinkSendsTheSamePayloadAsBefore()
    {
        var requests = new List<(HttpRequestMessage Request, JsonObject Body)>();
        var sut = MeetGateway(new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add((request, ReadBody(request)));
            return CalendarCreated("event-plain");
        })));

        var result = await ExecuteCalendarCreateAsync(sut, new JsonObject
        {
            ["summary"] = "Roadmap",
            ["start"] = "2026-09-24T10:00:00+07:00",
            ["end"] = "2026-09-24T10:30:00+07:00",
            ["description"] = "Plan",
        });

        Assert.True(result.IsSuccess);
        var (request, body) = Assert.Single(requests);
        Assert.Equal("https://google.test/calendar/v3/calendars/primary/events", request.RequestUri!.ToString());
        Assert.Equal("Plan", body["description"]!.GetValue<string>());
        Assert.Null(body["conferenceData"]);
        Assert.Null(body["location"]);
        Assert.Null(body["attendees"]);
        Assert.Null(body["start"]!["timeZone"]);

        var data = result.Data!;
        Assert.Equal("event-plain", result.ProviderResourceRef);
        Assert.Equal("google_calendar", data["provider"]!.GetValue<string>());
        Assert.Equal("event-plain", data["eventId"]!.GetValue<string>());
        Assert.Equal("https://calendar.google.test/event-plain", data["htmlLink"]!.GetValue<string>());
        Assert.Null(data["meetLink"]);
        Assert.False(data["conferenceAttached"]!.GetValue<bool>());
        Assert.Equal("event-plain", data["event"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateCalendarEvent_AttachesAnExistingMeetLinkAsConferenceData()
    {
        var requests = new List<(HttpRequestMessage Request, JsonObject Body)>();
        var sut = MeetGateway(new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add((request, ReadBody(request)));
            return CalendarCreated("event-meet", withConference: true);
        })));

        var result = await ExecuteCalendarCreateAsync(sut, new JsonObject
        {
            ["summary"] = "Customer sync",
            ["start"] = "2026-09-24T10:00:00",
            ["end"] = "2026-09-24T10:30:00",
            ["description"] = "Agenda",
            ["meetLink"] = "https://meet.google.com/abc-mnop-xyz",
            ["attendees"] = new JsonArray("nhi@example.com"),
        });

        Assert.True(result.IsSuccess);
        var (request, body) = Assert.Single(requests);
        Assert.Contains("conferenceDataVersion=1", request.RequestUri!.Query);
        Assert.Contains("sendUpdates=all", request.RequestUri!.Query);

        var conference = body["conferenceData"]!;
        // Code derived from the link when the caller did not pass one.
        Assert.Equal("abc-mnop-xyz", conference["conferenceId"]!.GetValue<string>());
        Assert.Equal("hangoutsMeet", conference["conferenceSolution"]!["key"]!["type"]!.GetValue<string>());
        var entryPoint = conference["entryPoints"]!.AsArray()[0]!;
        Assert.Equal("video", entryPoint["entryPointType"]!.GetValue<string>());
        Assert.Equal("https://meet.google.com/abc-mnop-xyz", entryPoint["uri"]!.GetValue<string>());
        Assert.Equal("meet.google.com/abc-mnop-xyz", entryPoint["label"]!.GetValue<string>());
        Assert.Null(conference["createRequest"]);

        Assert.Equal("https://meet.google.com/abc-mnop-xyz", body["location"]!.GetValue<string>());
        Assert.Contains("Agenda", body["description"]!.GetValue<string>());
        Assert.Contains("https://meet.google.com/abc-mnop-xyz", body["description"]!.GetValue<string>());
        Assert.Equal("nhi@example.com", body["attendees"]!.AsArray()[0]!["email"]!.GetValue<string>());
        // A bare time on a Meet-linked event gets the workspace zone (the Meet tool echoes as-is).
        Assert.Equal("Asia/Ho_Chi_Minh", body["start"]!["timeZone"]!.GetValue<string>());

        var data = result.Data!;
        Assert.Equal("google_calendar", data["provider"]!.GetValue<string>());
        Assert.Equal("https://meet.google.com/abc-mnop-xyz", data["meetLink"]!.GetValue<string>());
        Assert.Equal("https://meet.google.com/abc-mnop-xyz", data["hangoutLink"]!.GetValue<string>());
        Assert.True(data["conferenceAttached"]!.GetValue<bool>());
        Assert.Equal("Customer sync", data["summary"]!.GetValue<string>());
        Assert.Equal("2026-09-24T10:00:00+07:00", data["start"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateCalendarEvent_RetriesOnceWithoutConferenceDataOnBadRequest()
    {
        var requests = new List<(HttpRequestMessage Request, JsonObject Body)>();
        var sut = MeetGateway(new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add((request, ReadBody(request)));
            return requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"code":400,"message":"Invalid conference type value."}}"""),
                }
                : CalendarCreated("event-fallback");
        })));

        var result = await ExecuteCalendarCreateAsync(sut, new JsonObject
        {
            ["summary"] = "Customer sync",
            ["start"] = "2026-09-24T10:00:00+07:00",
            ["end"] = "2026-09-24T10:30:00+07:00",
            ["meetLink"] = "https://meet.google.com/abc-mnop-xyz",
            ["meetingCode"] = "abc-mnop-xyz",
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, requests.Count);
        Assert.NotNull(requests[0].Body["conferenceData"]);
        Assert.Null(requests[1].Body["conferenceData"]);
        Assert.DoesNotContain("conferenceDataVersion", requests[1].Request.RequestUri!.Query);
        Assert.DoesNotContain("sendUpdates", requests[1].Request.RequestUri!.Query);
        Assert.Equal("https://meet.google.com/abc-mnop-xyz", requests[1].Body["location"]!.GetValue<string>());
        Assert.Contains("https://meet.google.com/abc-mnop-xyz", requests[1].Body["description"]!.GetValue<string>());
        Assert.False(result.Data!["conferenceAttached"]!.GetValue<bool>());
        Assert.Equal("https://meet.google.com/abc-mnop-xyz", result.Data["meetLink"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateCalendarEvent_DoesNotRetryASecondBadRequest()
    {
        var calls = 0;
        var sut = MeetGateway(new HttpClient(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":400,"message":"Bad start."}}"""),
            };
        })));

        var result = await ExecuteCalendarCreateAsync(sut, new JsonObject
        {
            ["summary"] = "Customer sync",
            ["start"] = "nope",
            ["end"] = "nope",
            ["meetLink"] = "https://meet.google.com/abc-mnop-xyz",
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(2, calls);
    }

    private static JsonObject ReadBody(HttpRequestMessage request)
    {
        return JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
    }

    private static HttpResponseMessage CalendarCreated(string eventId, bool withConference = false)
    {
        var json = new JsonObject
        {
            ["id"] = eventId,
            ["summary"] = "Customer sync",
            ["htmlLink"] = $"https://calendar.google.test/{eventId}",
            ["start"] = new JsonObject { ["dateTime"] = "2026-09-24T10:00:00+07:00" },
            ["end"] = new JsonObject { ["dateTime"] = "2026-09-24T10:30:00+07:00" },
        };
        if (withConference)
        {
            json["hangoutLink"] = "https://meet.google.com/abc-mnop-xyz";
            json["conferenceData"] = new JsonObject { ["conferenceId"] = "abc-mnop-xyz" };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(json) };
    }

    private static GoogleWorkspaceMcpToolGateway MeetGateway(HttpClient httpClient)
    {
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        return new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions
            {
                CalendarEventsEndpointFormat = "https://google.test/calendar/v3/calendars/{0}/events",
                MeetSpacesEndpoint = "https://google.test/meet/v2/spaces",
            }));
    }

    private static Task<McpToolExecutionResult> ExecuteMeetAsync(GoogleWorkspaceMcpToolGateway sut, JsonObject arguments)
    {
        return sut.ExecuteAsync(
            GoogleDefinition("google_meet", PluginConstants.Providers.Google),
            MeetCreateTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                "google_meet",
                "google_calendar_create_meet_event",
                arguments,
                null,
                null,
                null));
    }

    private static Task<McpToolExecutionResult> ExecuteCalendarCreateAsync(GoogleWorkspaceMcpToolGateway sut, JsonObject arguments)
    {
        return sut.ExecuteAsync(
            GoogleDefinition("google_calendar", PluginConstants.Providers.Google),
            CalendarCreateTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                "google_calendar",
                "google_calendar_create_event",
                arguments,
                null,
                null,
                null));
    }

    // ---- WT-646: the gateway serves a provider, not one catalog row --------------------------

    [Theory]
    [InlineData("google_drive")]
    [InlineData("google_calendar")]
    [InlineData("google_meet")]
    public async Task ExecuteAsync_ServesEveryGoogleCatalogRow(string pluginKey)
    {
        // Before WT-646 this compared the key against 'google_workspace'. After the split that
        // matched none of the three live rows, so every Google tool call answered unknown_plugin.
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject { ["files"] = new JsonArray() }),
            }));
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        var sut = new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions
            {
                DriveFilesEndpoint = "https://google.test/drive/v3/files",
            }));

        var result = await sut.ExecuteAsync(
            GoogleDefinition(pluginKey, PluginConstants.Providers.Google),
            DriveSearchTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                pluginKey,
                "google_drive_search",
                new JsonObject { ["query"] = "roadmap" },
                null,
                null,
                null));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ExecuteAsync_RefusesARowFromAnotherProvider()
    {
        // The assertion that has to survive the loosening: this gateway calls Google's APIs with
        // whatever token it is handed, so a row belonging to anyone else must be turned away
        // before the token is used.
        var protector = Substitute.For<IPluginCredentialProtector>();
        var sut = new GoogleWorkspaceMcpToolGateway(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            protector,
            Options.Create(new GoogleWorkspaceApiOptions()));

        var result = await sut.ExecuteAsync(
            GoogleDefinition("remote_app", "remote_app"),
            DriveSearchTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                "remote_app",
                "google_drive_search",
                new JsonObject { ["query"] = "roadmap" },
                null,
                null,
                null));

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.UnknownPlugin, result.ErrorCode);
        // And crucially, the stored token was never even decrypted.
        protector.DidNotReceive().Unprotect(Arg.Any<string>());
    }

    private static PluginDefinitionDto GoogleDefinition(string pluginKey, string provider)
    {
        return new PluginDefinitionDto(
            Guid.NewGuid(),
            pluginKey,
            provider,
            pluginKey,
            pluginKey,
            null,
            [],
            []);
    }

    private static PluginDefinitionDto GoogleDriveDefinition()
    {
        return new PluginDefinitionDto(
            Guid.NewGuid(),
            GoogleDriveKey,
            PluginConstants.Providers.Google,
            "Google Drive",
            "Search your Google Drive and read the contents of a file.",
            null,
            [],
            []);
    }

    private static McpToolDescriptorDto DriveSearchTool()
    {
        return new McpToolDescriptorDto(
            "google_drive_search",
            GoogleDriveKey,
            "Search Google Drive",
            "Search files in Google Drive.",
            PluginConstants.ToolEffect.Read,
            [],
            new JsonObject());
    }

    private static McpToolDescriptorDto DriveGetFileTool()
    {
        return new McpToolDescriptorDto(
            "google_drive_get_file",
            GoogleDriveKey,
            "Read Google Drive file",
            "Read supported text content from a Google Drive file.",
            PluginConstants.ToolEffect.Read,
            [],
            new JsonObject());
    }

    private static McpToolDescriptorDto MeetCreateTool()
    {
        return new McpToolDescriptorDto(
            "google_calendar_create_meet_event",
            "google_meet",
            "Create Google Meet meeting",
            "Create a Google Meet meeting link (no calendar event).",
            PluginConstants.ToolEffect.Write,
            ["https://www.googleapis.com/auth/meetings.space.created"],
            new JsonObject());
    }

    private static McpToolDescriptorDto CalendarCreateTool()
    {
        return new McpToolDescriptorDto(
            "google_calendar_create_event",
            "google_calendar",
            "Create Google Calendar event",
            "Create an event in the connected Google Calendar account after user confirmation.",
            PluginConstants.ToolEffect.Write,
            ["https://www.googleapis.com/auth/calendar.events"],
            new JsonObject());
    }

    private class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
