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
            GoogleWorkspaceDefinition(),
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
                PluginConstants.GoogleWorkspace,
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
            GoogleWorkspaceDefinition(),
            DriveSearchTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                PluginConstants.GoogleWorkspace,
                "google_drive_search",
                new JsonObject { ["query"] = "roadmap" },
                null,
                null,
                null));

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.ErrorCode);
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
            GoogleWorkspaceDefinition(),
            DriveGetFileTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                PluginConstants.GoogleWorkspace,
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
            GoogleWorkspaceDefinition(),
            DriveGetFileTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                PluginConstants.GoogleWorkspace,
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
            GoogleWorkspaceDefinition(),
            DriveGetFileTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                PluginConstants.GoogleWorkspace,
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
            GoogleWorkspaceDefinition(),
            DriveGetFileTool(),
            new PluginConnection { EncryptedAccessToken = "encrypted-access-token" },
            new McpToolExecutionRequest(
                null,
                PluginConstants.GoogleWorkspace,
                "google_drive_get_file",
                new JsonObject { ["fileId"] = "text-1" },
                null,
                null,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal("too_large", result.Data!["contentStatus"]!.GetValue<string>());
        Assert.Equal(1, requestCount);
    }

    private static PluginDefinitionDto GoogleWorkspaceDefinition()
    {
        return new PluginDefinitionDto(
            Guid.NewGuid(),
            PluginConstants.GoogleWorkspace,
            "Google Workspace",
            "Work across Drive and Calendar.",
            null,
            [],
            []);
    }

    private static McpToolDescriptorDto DriveSearchTool()
    {
        return new McpToolDescriptorDto(
            "google_drive_search",
            PluginConstants.GoogleWorkspace,
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
            PluginConstants.GoogleWorkspace,
            "Read Google Drive file",
            "Read supported text content from a Google Drive file.",
            PluginConstants.ToolEffect.Read,
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
