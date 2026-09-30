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

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_RequestsConferenceDataAndReturnsHangoutLink()
    {
        HttpRequestMessage? capturedRequest = null;
        JsonObject? capturedPayload = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            capturedPayload = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-1",
                    ["summary"] = "Customer sync",
                    ["htmlLink"] = "https://calendar.google.test/event-1",
                    ["hangoutLink"] = "https://meet.google.com/abc-defg-hij",
                    ["start"] = new JsonObject { ["dateTime"] = "2026-09-05T15:00:00+07:00" },
                    ["end"] = new JsonObject { ["dateTime"] = "2026-09-05T15:30:00+07:00" },
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
                CalendarEventsEndpointFormat = "https://google.test/calendar/v3/calendars/{0}/events",
            }));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            MeetCreateTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                GoogleDriveKey,
                "google_calendar_create_meet_event",
                new JsonObject
                {
                    ["summary"] = "Customer sync",
                    ["start"] = "2026-09-05T15:00:00+07:00",
                    ["end"] = "2026-09-05T15:30:00+07:00",
                    ["timeZone"] = "Asia/Bangkok",
                    ["description"] = "Quarterly customer sync",
                    ["attendees"] = new JsonArray("nhi@example.com", "tu@example.com"),
                },
                null,
                null,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal("event-1", result.ProviderResourceRef);
        Assert.Equal("google_meet", result.Data!["provider"]!.GetValue<string>());
        Assert.Equal("event-1", result.Data["eventId"]!.GetValue<string>());
        Assert.Equal("https://meet.google.com/abc-defg-hij", result.Data["meetLink"]!.GetValue<string>());
        Assert.Equal("success", result.Data["meetLinkStatus"]!.GetValue<string>());
        Assert.Contains("conferenceDataVersion=1", capturedRequest!.RequestUri!.Query);

        var payload = capturedPayload!;
        Assert.Equal("Customer sync", payload["summary"]!.GetValue<string>());
        Assert.Equal("Asia/Bangkok", payload["start"]!["timeZone"]!.GetValue<string>());
        Assert.Equal("hangoutsMeet", payload["conferenceData"]!["createRequest"]!["conferenceSolutionKey"]!["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(payload["conferenceData"]!["createRequest"]!["requestId"]!.GetValue<string>()));
        Assert.Equal("nhi@example.com", payload["attendees"]!.AsArray()[0]!["email"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_FallsBackToConferenceEntryPointWhenHangoutLinkIsMissing()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-2",
                    ["htmlLink"] = "https://calendar.google.test/event-2",
                    ["conferenceData"] = new JsonObject
                    {
                        ["entryPoints"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["entryPointType"] = "video",
                                ["uri"] = "https://meet.google.com/xyz-abcd-efg",
                            },
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
                CalendarEventsEndpointFormat = "https://google.test/calendar/v3/calendars/{0}/events",
            }));

        var result = await sut.ExecuteAsync(
            GoogleDriveDefinition(),
            MeetCreateTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                GoogleDriveKey,
                "google_calendar_create_meet_event",
                new JsonObject
                {
                    ["summary"] = "Customer sync",
                    ["start"] = "2026-09-05T15:00:00+07:00",
                    ["end"] = "2026-09-05T15:30:00+07:00",
                },
                null,
                null,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal("https://meet.google.com/xyz-abcd-efg", result.Data!["meetLink"]!.GetValue<string>());
        Assert.Equal("success", result.Data["meetLinkStatus"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_RejectsUnparseableStartWithoutEndBeforeProviderCall()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Provider must not be called.")));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject { ["start"] = "next tuesday-ish" });

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.UnknownTool, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_DefaultsToNowForThirtyMinutesWithDefaultTitle()
    {
        JsonObject? capturedPayload = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedPayload = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-now",
                    ["summary"] = "Google Meet meeting",
                    ["hangoutLink"] = "https://meet.google.com/abc-defg-hij",
                    ["start"] = new JsonObject { ["dateTime"] = "2026-09-17T08:42:00Z" },
                    ["end"] = new JsonObject { ["dateTime"] = "2026-09-17T09:12:00Z" },
                }),
            };
        }));
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 17, 8, 42, 37, 512, TimeSpan.Zero));
        var sut = MeetGateway(httpClient, clock);

        var result = await ExecuteMeetAsync(sut, new JsonObject());

        Assert.True(result.IsSuccess);
        var payload = capturedPayload!;
        Assert.Equal("Google Meet meeting", payload["summary"]!.GetValue<string>());
        Assert.Equal("2026-09-17T08:42:00Z", payload["start"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("2026-09-17T09:12:00Z", payload["end"]!["dateTime"]!.GetValue<string>());
        Assert.Null(payload["start"]!["timeZone"]);
        Assert.Equal("hangoutsMeet", payload["conferenceData"]!["createRequest"]!["conferenceSolutionKey"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_EndDefaultsToThirtyMinutesAfterGivenStartKeepingOffset()
    {
        JsonObject? capturedPayload = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedPayload = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-3",
                    ["hangoutLink"] = "https://meet.google.com/abc-defg-hij",
                }),
            };
        }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject { ["start"] = "2026-09-18T15:00:00+07:00" });

        Assert.True(result.IsSuccess);
        Assert.Equal("2026-09-18T15:30:00+07:00", capturedPayload!["end"]!["dateTime"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_EndKeepsNoOffsetWhenStartHasNone()
    {
        JsonObject? capturedPayload = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedPayload = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-3b",
                    ["hangoutLink"] = "https://meet.google.com/abc-defg-hij",
                }),
            };
        }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject
        {
            ["start"] = "2026-09-18T15:00:00",
            ["timeZone"] = "Asia/Bangkok",
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("2026-09-18T15:30:00", capturedPayload!["end"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("Asia/Bangkok", capturedPayload["end"]!["timeZone"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_StampsTheWorkspaceZoneOnATimeThatCarriesNone()
    {
        // Google refuses a date-time with neither an offset nor a timeZone, and a model told the
        // local date sends exactly that. The zone stamped here is the one WarpBot's confirmation
        // card prints, so what the user read and what was booked are the same moment.
        JsonObject? capturedPayload = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedPayload = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-3c",
                    ["hangoutLink"] = "https://meet.google.com/abc-defg-hij",
                }),
            };
        }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject
        {
            ["summary"] = "Roadmap",
            ["start"] = "2026-09-24T10:00:00",
            ["end"] = "2026-09-24T10:30:00",
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("Asia/Ho_Chi_Minh", capturedPayload!["start"]!["timeZone"]!.GetValue<string>());
        Assert.Equal("Asia/Ho_Chi_Minh", capturedPayload["end"]!["timeZone"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_LeavesAnOffsetTimeWithoutAZone()
    {
        JsonObject? capturedPayload = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedPayload = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-3d",
                    ["hangoutLink"] = "https://meet.google.com/abc-defg-hij",
                }),
            };
        }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject
        {
            ["start"] = "2026-09-24T10:00:00+07:00",
            ["end"] = "2026-09-24T10:30:00+07:00",
        });

        Assert.True(result.IsSuccess);
        Assert.Null(capturedPayload!["start"]!["timeZone"]);
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_KeepsTheCreatedEventWhenTheRereadFails()
    {
        // The meeting exists by then; reporting a failure would have the user book a second one.
        var calls = 0;
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new JsonObject
                    {
                        ["id"] = "event-3e",
                        ["conferenceData"] = new JsonObject
                        {
                            ["createRequest"] = new JsonObject
                            {
                                ["status"] = new JsonObject { ["statusCode"] = "pending" },
                            },
                        },
                    }),
                };
            }

            throw new HttpRequestException("network went away");
        }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject { ["summary"] = "Roadmap" });

        Assert.True(result.IsSuccess);
        Assert.Equal("event-3e", result.Data!["eventId"]!.GetValue<string>());
        Assert.Equal("pending", result.Data["meetLinkStatus"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_TakesMeetingCodeFromConferenceId()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-4",
                    ["hangoutLink"] = "https://meet.google.com/abc-defg-hij",
                    ["conferenceData"] = new JsonObject
                    {
                        ["conferenceId"] = "xyz-wxyz-xyz",
                        ["createRequest"] = new JsonObject
                        {
                            ["status"] = new JsonObject { ["statusCode"] = "success" },
                        },
                    },
                }),
            }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject { ["summary"] = "Sync", ["timeZone"] = "Asia/Bangkok" });

        Assert.True(result.IsSuccess);
        Assert.Equal("xyz-wxyz-xyz", result.Data!["meetingCode"]!.GetValue<string>());
        Assert.Equal("Asia/Bangkok", result.Data["timeZone"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_ParsesMeetingCodeFromHangoutLinkWhenConferenceIdIsMissing()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-5",
                    ["hangoutLink"] = "https://meet.google.com/abc-defg-hij?authuser=0",
                }),
            }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject());

        Assert.True(result.IsSuccess);
        Assert.Equal("abc-defg-hij", result.Data!["meetingCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_RereadsPendingConferenceUntilLinkArrives()
    {
        var requests = new List<HttpRequestMessage>();
        var responses = new Queue<HttpResponseMessage>([
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-6",
                    ["htmlLink"] = "https://calendar.google.test/event-6",
                    ["conferenceData"] = new JsonObject
                    {
                        ["createRequest"] = new JsonObject
                        {
                            ["status"] = new JsonObject { ["statusCode"] = "pending" },
                        },
                    },
                }),
            },
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-6",
                    ["htmlLink"] = "https://calendar.google.test/event-6",
                    ["hangoutLink"] = "https://meet.google.com/pen-ding-now",
                    ["conferenceData"] = new JsonObject
                    {
                        ["conferenceId"] = "pen-ding-now",
                        ["createRequest"] = new JsonObject
                        {
                            ["status"] = new JsonObject { ["statusCode"] = "success" },
                        },
                    },
                }),
            },
        ]);
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return responses.Dequeue();
        }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject());

        Assert.True(result.IsSuccess);
        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal(HttpMethod.Get, requests[1].Method);
        Assert.Equal("/calendar/v3/calendars/primary/events/event-6", requests[1].RequestUri!.AbsolutePath);
        Assert.Contains("conferenceDataVersion=1", requests[1].RequestUri!.Query);
        Assert.Equal("https://meet.google.com/pen-ding-now", result.Data!["meetLink"]!.GetValue<string>());
        Assert.Equal("pen-ding-now", result.Data["meetingCode"]!.GetValue<string>());
        Assert.Equal("success", result.Data["meetLinkStatus"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CreateMeetEvent_ReportsPendingAfterBoundedRereads()
    {
        var requestCount = 0;
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["id"] = "event-7",
                    ["conferenceData"] = new JsonObject
                    {
                        ["createRequest"] = new JsonObject
                        {
                            ["status"] = new JsonObject { ["statusCode"] = "pending" },
                        },
                    },
                }),
            };
        }));
        var sut = MeetGateway(httpClient);

        var result = await ExecuteMeetAsync(sut, new JsonObject());

        Assert.True(result.IsSuccess);
        // One insert plus the three bounded re-reads.
        Assert.Equal(4, requestCount);
        Assert.Equal("pending", result.Data!["meetLinkStatus"]!.GetValue<string>());
        Assert.Null(result.Data["meetLink"]);
        Assert.Null(result.Data["meetingCode"]);
    }

    private static GoogleWorkspaceMcpToolGateway MeetGateway(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("encrypted-access-token").Returns("plain-access-token");
        return new GoogleWorkspaceMcpToolGateway(
            httpClient,
            protector,
            Options.Create(new GoogleWorkspaceApiOptions
            {
                CalendarEventsEndpointFormat = "https://google.test/calendar/v3/calendars/{0}/events",
                MeetConferencePollDelayMilliseconds = 0,
            }),
            timeProvider);
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
            GoogleDriveKey,
            "Create Google Meet meeting",
            "Create a Google Calendar event with a Google Meet link.",
            PluginConstants.ToolEffect.Write,
            [],
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
