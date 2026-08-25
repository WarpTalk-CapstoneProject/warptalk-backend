using System.Linq.Expressions;
using System.Text;
using System.Text.Json.Nodes;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

public class McpToolOrchestratorTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PluginId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IMcpToolGateway _gateway = Substitute.For<IMcpToolGateway>();
    private readonly IWorkspacePluginPolicyClient _workspacePolicy = Substitute.For<IWorkspacePluginPolicyClient>();
    private readonly IPluginTokenRefresher _tokenRefresher = Substitute.For<IPluginTokenRefresher>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginRepository _pluginRepository = Substitute.For<IPluginRepository>();
    private readonly IPluginInstallationRepository _installationRepository = Substitute.For<IPluginInstallationRepository>();
    private readonly IPluginConnectionRepository _connectionRepository = Substitute.For<IPluginConnectionRepository>();
    private readonly IPluginToolAuditRepository _auditRepository = Substitute.For<IPluginToolAuditRepository>();

    public McpToolOrchestratorTests()
    {
        _unitOfWork.PluginRepository.Returns(_pluginRepository);
        _unitOfWork.PluginInstallationRepository.Returns(_installationRepository);
        _unitOfWork.PluginConnectionRepository.Returns(_connectionRepository);
        _unitOfWork.PluginToolAuditRepository.Returns(_auditRepository);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);
    }

    [Fact]
    public async Task ListAvailableToolsAsync_ReturnsNoTools_WhenWorkspaceDisallowsPersonalPlugins()
    {
        _workspacePolicy.AllowsPluginUsageAsync(WorkspaceId, Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = CreateSut();

        var result = await sut.ListAvailableToolsAsync(UserId, WorkspaceId);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
        await _installationRepository.DidNotReceive()
            .FindAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RecordsPermissionDenied_WhenWorkspaceDisallowsPersonalPlugins()
    {
        var plugin = GoogleWorkspacePlugin();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _workspacePolicy.AllowsPluginUsageAsync(WorkspaceId, Arg.Any<CancellationToken>())
            .Returns(false);

        var request = Request("google_drive_search");
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(UserId, request);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.Value.ErrorCode);
        await _auditRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginToolAudit>(audit =>
                    audit.WorkspaceId == WorkspaceId
                    && audit.UserId == UserId
                    && audit.PluginId == PluginId
                    && audit.ResultStatus == PluginConstants.ErrorCodes.PermissionDenied),
                Arg.Any<CancellationToken>());
        await _gateway.DidNotReceive()
            .ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UsesPersonalInstallAndConnection_WhenWorkspaceAllowsPlugins()
    {
        var plugin = GoogleWorkspacePlugin();
        ConfigureInstalledConnected(plugin);
        _gateway.ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new McpToolExecutionResult(true, null, null, new JsonObject { ["ok"] = true }, "drive:file", null));

        var request = Request("google_drive_search");
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(UserId, request);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsSuccess);
        await _gateway.Received(1)
            .ExecuteAsync(
                Arg.Is<PluginDefinitionDto>(definition => definition.Id == PluginId),
                Arg.Is<McpToolDescriptorDto>(tool => tool.Name == "google_drive_search"),
                Arg.Is<PluginConnection>(connection =>
                    connection.UserId == UserId
                    && connection.PluginId == PluginId
                    && connection.Status == PluginConstants.ConnectionStatus.Connected),
                request,
                Arg.Any<CancellationToken>());
        await _auditRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginToolAudit>(audit =>
                    audit.ResultStatus == "success"
                    && audit.ProviderResourceRef == "drive:file"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RequiresConfirmationBeforeWriteTool()
    {
        var plugin = GoogleWorkspacePlugin(includeWriteTool: true);
        ConfigureInstalledConnected(plugin);
        var request = Request("google_calendar_create_event");
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(UserId, request);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConfirmationRequired, result.Value.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ConfirmationToken));
        await _gateway.DidNotReceive()
            .ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RejectsConfirmationTokenForDifferentAction()
    {
        var plugin = GoogleWorkspacePlugin(includeWriteTool: true);
        ConfigureInstalledConnected(plugin);
        var confirmedDifferentAction = Request(
            "google_calendar_create_event",
            new JsonObject { ["summary"] = "Different meeting" });
        var replayedRequest = Request(
            "google_calendar_create_event",
            new JsonObject { ["summary"] = "Roadmap review" },
            ConfirmationToken(UserId, confirmedDifferentAction));
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(UserId, replayedRequest);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.Value.ErrorCode);
        await _gateway.DidNotReceive()
            .ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AllowsWriteTool_WhenConfirmationTokenMatchesAction()
    {
        var plugin = GoogleWorkspacePlugin(includeWriteTool: true);
        ConfigureInstalledConnected(plugin);
        var unconfirmed = Request(
            "google_calendar_create_event",
            new JsonObject { ["summary"] = "Roadmap review" });
        var confirmed = unconfirmed with { ConfirmationToken = ConfirmationToken(UserId, unconfirmed) };
        _gateway.ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new McpToolExecutionResult(true, null, null, new JsonObject { ["ok"] = true }, "calendar:event", null));
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(UserId, confirmed);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsSuccess);
        await _gateway.Received(1)
            .ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Is<McpToolDescriptorDto>(tool => tool.Name == "google_calendar_create_event"),
                Arg.Any<PluginConnection>(),
                confirmed,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RefreshesExpiredAccessToken_ThenExecutesTool()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(-5));
        _tokenRefresher.RefreshAccessTokenAsync(plugin, connection, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _gateway.ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new McpToolExecutionResult(true, null, null, new JsonObject { ["ok"] = true }, "drive:file", null));

        var result = await CreateSut().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsSuccess);
        await _tokenRefresher.Received(1)
            .RefreshAccessTokenAsync(plugin, connection, Arg.Any<CancellationToken>());
        await _gateway.Received(1)
            .ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>());
        await _auditRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginToolAudit>(audit => audit.ResultStatus == "success"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRefresh_WhenAccessTokenIsStillValid()
    {
        var plugin = GoogleWorkspacePlugin();
        ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(30));
        _gateway.ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new McpToolExecutionResult(true, null, null, new JsonObject { ["ok"] = true }, null, null));

        var result = await CreateSut().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.True(result.Value!.IsSuccess);
        await _tokenRefresher.DidNotReceive()
            .RefreshAccessTokenAsync(
                Arg.Any<Plugin>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsConnectionRequired_WhenExpiredAccessTokenCannotBeRefreshed()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(-5));
        _tokenRefresher.RefreshAccessTokenAsync(plugin, connection, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Refresh failed.", PluginConstants.ErrorCodes.ConnectionRequired));

        var result = await CreateSut().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.Value.ErrorCode);
        await _gateway.DidNotReceive()
            .ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>());
        await _auditRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginToolAudit>(audit =>
                    audit.ResultStatus == PluginConstants.ErrorCodes.ConnectionRequired),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RefreshesAndRetriesOnce_WhenProviderRejectsStoredAccessToken()
    {
        // The stored expiry can lag reality (clock skew, a grant refreshed elsewhere), so a 401
        // from the provider is the second trigger for a refresh.
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(30));
        _tokenRefresher.RefreshAccessTokenAsync(plugin, connection, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _gateway.ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new McpToolExecutionResult(false, PluginConstants.ErrorCodes.ConnectionRequired, "401", null, null, null),
                new McpToolExecutionResult(true, null, null, new JsonObject { ["ok"] = true }, "drive:file", null));

        var result = await CreateSut().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.True(result.Value!.IsSuccess);
        await _tokenRefresher.Received(1)
            .RefreshAccessTokenAsync(plugin, connection, Arg.Any<CancellationToken>());
        await _gateway.Received(2)
            .ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>());
        await _auditRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginToolAudit>(audit => audit.ResultStatus == "success"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RefreshesAtMostOncePerExecution_WhenRetryIsStillUnauthorized()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(-5));
        _tokenRefresher.RefreshAccessTokenAsync(plugin, connection, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _gateway.ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new McpToolExecutionResult(false, PluginConstants.ErrorCodes.ConnectionRequired, "401", null, null, null));

        var result = await CreateSut().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.Value.ErrorCode);
        // Refreshed once up front because the token had expired; the 401 must not trigger a second
        // refresh, otherwise a permanently revoked grant loops against the provider.
        await _tokenRefresher.Received(1)
            .RefreshAccessTokenAsync(
                Arg.Any<Plugin>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<CancellationToken>());
        await _gateway.Received(1)
            .ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>());
    }

    private McpToolOrchestrator CreateSut()
    {
        return new McpToolOrchestrator(_gateway, _unitOfWork, _workspacePolicy, _tokenRefresher);
    }

    private McpToolExecutionRequest Request(string toolName)
    {
        return Request(toolName, new JsonObject { ["query"] = "roadmap" }, null);
    }

    private static McpToolExecutionRequest Request(
        string toolName,
        JsonObject arguments,
        string? confirmationToken = null)
    {
        return new McpToolExecutionRequest(
            WorkspaceId,
            PluginConstants.GoogleWorkspace,
            toolName,
            arguments,
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            null,
            confirmationToken);
    }

    private PluginConnection ConfigureInstalledConnected(Plugin plugin, DateTime? accessTokenExpiresAt = null)
    {
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _workspacePolicy.AllowsPluginUsageAsync(WorkspaceId, Arg.Any<CancellationToken>())
            .Returns(true);
        _installationRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new PluginInstallation
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                PluginId = PluginId,
                Status = PluginConstants.InstallationStatus.Installed,
                InstalledAt = DateTime.UtcNow,
            });
        var connection = new PluginConnection
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            PluginId = PluginId,
            Status = PluginConstants.ConnectionStatus.Connected,
            EncryptedAccessToken = "protected:access-token",
            EncryptedRefreshToken = "protected:refresh-token",
            AccessTokenExpiresAt = accessTokenExpiresAt ?? DateTime.UtcNow.AddMinutes(30),
            ScopesJson = """
                [
                  "https://www.googleapis.com/auth/drive.readonly",
                  "https://www.googleapis.com/auth/calendar.events"
                ]
                """,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(connection);
        return connection;
    }

    private static string ConfirmationToken(Guid userId, McpToolExecutionRequest request)
    {
        var raw = $"{userId}:{request.WorkspaceId}:{request.PluginKey}:{request.ToolName}:{request.Arguments?.ToJsonString()}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static Plugin GoogleWorkspacePlugin(bool includeWriteTool = false)
    {
        var toolsJson = includeWriteTool
            ? """
                [
                  {
                    "name": "google_drive_search",
                    "pluginKey": "google_workspace",
                    "label": "Search Google Drive",
                    "description": "Search files in Google Drive.",
                    "effect": "read",
                    "requiredScopes": ["https://www.googleapis.com/auth/drive.readonly"],
                    "parameters": {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string" }
                      },
                      "required": ["query"]
                    }
                  },
                  {
                    "name": "google_calendar_create_event",
                    "pluginKey": "google_workspace",
                    "label": "Create Google Calendar event",
                    "description": "Create a Google Calendar event.",
                    "effect": "write",
                    "requiredScopes": ["https://www.googleapis.com/auth/calendar.events"],
                    "parameters": {
                      "type": "object",
                      "properties": {
                        "summary": { "type": "string" }
                      },
                      "required": ["summary"]
                    }
                  }
                ]
                """
            : """
                [
                  {
                    "name": "google_drive_search",
                    "pluginKey": "google_workspace",
                    "label": "Search Google Drive",
                    "description": "Search files in Google Drive.",
                    "effect": "read",
                    "requiredScopes": ["https://www.googleapis.com/auth/drive.readonly"],
                    "parameters": {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string" }
                      },
                      "required": ["query"]
                    }
                  }
                ]
                """;

        return new Plugin
        {
            Id = PluginId,
            PluginKey = PluginConstants.GoogleWorkspace,
            Label = "Google Workspace",
            Description = "Work across Google Drive and Calendar.",
            Provider = "google",
            AvatarUrl = "https://example.test/google.svg",
            IsActive = true,
            RequiredScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
            ToolsJson = toolsJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
