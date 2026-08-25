using System.Linq.Expressions;
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
    private readonly IMcpConfirmationTokenService _confirmationTokenService = Substitute.For<IMcpConfirmationTokenService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginRepository _pluginRepository = Substitute.For<IPluginRepository>();
    private readonly IPluginInstallationRepository _installationRepository = Substitute.For<IPluginInstallationRepository>();
    private readonly IPluginConnectionRepository _connectionRepository = Substitute.For<IPluginConnectionRepository>();
    private readonly IPluginToolAuditRepository _auditRepository = Substitute.For<IPluginToolAuditRepository>();
    private readonly IPluginConfirmationTokenRepository _confirmationTokenRepository = Substitute.For<IPluginConfirmationTokenRepository>();
    private readonly IPluginOAuthClient _oauthClient = Substitute.For<IPluginOAuthClient>();
    private readonly IPluginOAuthStateProtector _stateProtector = Substitute.For<IPluginOAuthStateProtector>();
    private readonly IPluginCredentialProtector _credentialProtector = Substitute.For<IPluginCredentialProtector>();

    public McpToolOrchestratorTests()
    {
        _unitOfWork.PluginRepository.Returns(_pluginRepository);
        _unitOfWork.PluginInstallationRepository.Returns(_installationRepository);
        _unitOfWork.PluginConnectionRepository.Returns(_connectionRepository);
        _unitOfWork.PluginToolAuditRepository.Returns(_auditRepository);
        _unitOfWork.PluginConfirmationTokenRepository.Returns(_confirmationTokenRepository);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);
        _credentialProtector.Protect(Arg.Any<string>()).Returns(call => $"protected:{call.Arg<string>()}");
        _credentialProtector.Unprotect(Arg.Any<string>())
            .Returns(call => call.Arg<string>().Replace("protected:", "", StringComparison.Ordinal));
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
        _confirmationTokenService.CreateAsync(UserId, PluginId, request, Arg.Any<CancellationToken>())
            .Returns(Result.Success("signed-confirmation-token"));
        var sut = CreateSut();

        var result = await sut.ExecuteAsync(UserId, request);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConfirmationRequired, result.Value.ErrorCode);
        Assert.Equal("signed-confirmation-token", result.Value.ConfirmationToken);
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
        var replayedRequest = Request(
            "google_calendar_create_event",
            new JsonObject { ["summary"] = "Roadmap review" },
            "signed-confirmation-token");
        _confirmationTokenService.ValidateAndConsumeAsync(
                UserId,
                PluginId,
                replayedRequest,
                "signed-confirmation-token",
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure(
                "Confirmation token does not match this plugin action.",
                PluginConstants.ErrorCodes.PermissionDenied));
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
    public async Task ExecuteAsync_RejectsReplayedConfirmationToken()
    {
        var plugin = GoogleWorkspacePlugin(includeWriteTool: true);
        ConfigureInstalledConnected(plugin);
        var replayedRequest = Request(
            "google_calendar_create_event",
            new JsonObject { ["summary"] = "Roadmap review" },
            "signed-confirmation-token");
        _confirmationTokenService.ValidateAndConsumeAsync(
                UserId,
                PluginId,
                replayedRequest,
                "signed-confirmation-token",
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure(
                "Confirmation token has already been used.",
                PluginConstants.ErrorCodes.PermissionDenied));

        var result = await CreateSut().ExecuteAsync(UserId, replayedRequest);

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
    public async Task ExecuteAsync_ReturnsFreshConfirmationToken_WhenConfirmationTokenExpired()
    {
        var plugin = GoogleWorkspacePlugin(includeWriteTool: true);
        ConfigureInstalledConnected(plugin);
        var expiredRequest = Request(
            "google_calendar_create_event",
            new JsonObject { ["summary"] = "Roadmap review" },
            "expired-confirmation-token");
        _confirmationTokenService.ValidateAndConsumeAsync(
                UserId,
                PluginId,
                expiredRequest,
                "expired-confirmation-token",
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure(
                "Confirmation token expired. Confirm this action again.",
                PluginConstants.ErrorCodes.ConfirmationRequired));
        _confirmationTokenService.CreateAsync(UserId, PluginId, expiredRequest, Arg.Any<CancellationToken>())
            .Returns(Result.Success("fresh-confirmation-token"));

        var result = await CreateSut().ExecuteAsync(UserId, expiredRequest);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConfirmationRequired, result.Value.ErrorCode);
        Assert.Equal("fresh-confirmation-token", result.Value.ConfirmationToken);
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
        var confirmed = unconfirmed with { ConfirmationToken = "signed-confirmation-token" };
        _confirmationTokenService.ValidateAndConsumeAsync(
                UserId,
                PluginId,
                confirmed,
                "signed-confirmation-token",
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());
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

    // The cases below wire the *real* PluginConnectionService in as IPluginTokenRefresher and stub
    // only the provider client, because the behaviour under test spans both halves: the refresher
    // decides whether to write `expired`, the orchestrator decides which error code the caller and
    // the audit row see. A substituted refresher would let either half drift without a red test.

    [Fact]
    public async Task ExecuteAsync_MarksConnectionExpired_AndReturnsConnectionRequired_WhenProviderRejectsTheGrant()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(-5));
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultDto.GrantRejected(
                "Google token endpoint returned 400 (invalid_grant)."));

        var result = await CreateSutWithRealRefresher().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.Value.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Expired, connection.Status);
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
    public async Task ExecuteAsync_LeavesConnectionConnected_AndReturnsProviderUnavailable_WhenRefreshHitsProviderOutage()
    {
        // A Google 503 is not a verdict on the grant. Before this, it expired the row - and because
        // gate 5 rejects a non-connected row before the refresh code is reached, that was sticky:
        // one blip cost a full browser re-consent.
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(-5));
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultDto.ProviderUnavailable(
                "Google token endpoint returned 503."));

        var result = await CreateSutWithRealRefresher().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.Value.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
        Assert.Equal("protected:access-token", connection.EncryptedAccessToken);
        await _auditRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginToolAudit>(audit =>
                    audit.ResultStatus == PluginConstants.ErrorCodes.ProviderUnavailable),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LeavesConnectionConnected_AndReturnsProviderRateLimited_WhenRefreshIsThrottled()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(-5));
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultDto.ProviderRateLimited(
                "Google token endpoint returned 429 (rateLimitExceeded)."));

        var result = await CreateSutWithRealRefresher().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderRateLimited, result.Value.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
        await _auditRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginToolAudit>(audit =>
                    audit.ResultStatus == PluginConstants.ErrorCodes.ProviderRateLimited),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LeavesConnectionConnected_WhenRefreshRequestNeverReachesTheProvider()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(-5));
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns<PluginOAuthRefreshResultDto>(_ => throw new HttpRequestException("No such host is known."));

        var result = await CreateSutWithRealRefresher().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.Value.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
        _connectionRepository.DidNotReceive().Update(Arg.Any<PluginConnection>());
    }

    [Fact]
    public async Task ExecuteAsync_MarksConnectionExpired_WhenNothingIsStoredToRefreshWith()
    {
        // Regression guard on T037: a connection with no stored refresh token is dead by
        // construction, and must keep ending the connection rather than looking transient.
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(-5));
        connection.EncryptedRefreshToken = null;

        var result = await CreateSutWithRealRefresher().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.Value.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Expired, connection.Status);
        await _oauthClient.DidNotReceive()
            .RefreshAccessTokenAsync(Arg.Any<Plugin>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsProviderUnavailableRatherThanReconnect_WhenReactiveRefreshHitsProviderOutage()
    {
        // The gateway 401 said "this access token is stale"; the failed refresh said nothing at all
        // about the grant. Answering connection_required here would push the user through a browser
        // consent to fix a ten-second outage, so the transient code wins.
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(30));
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultDto.ProviderUnavailable(
                "Google token endpoint returned 503."));
        _gateway.ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new McpToolExecutionResult(false, PluginConstants.ErrorCodes.ConnectionRequired, "401", null, null, null));

        var result = await CreateSutWithRealRefresher().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.False(result.Value!.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.Value.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
        await _auditRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginToolAudit>(audit =>
                    audit.ResultStatus == PluginConstants.ErrorCodes.ProviderUnavailable),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StillRefreshesAtMostOnce_WhenAProactiveRefreshFailsTransiently()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(-5));
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultDto.ProviderUnavailable(
                "Google token endpoint returned 503."));

        var result = await CreateSutWithRealRefresher().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.Value!.ErrorCode);
        // One attempt, and the gateway is never called with a token we already know is stale.
        await _oauthClient.Received(1)
            .RefreshAccessTokenAsync(Arg.Any<Plugin>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gateway.DidNotReceive()
            .ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>());
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
    }

    [Fact]
    public async Task ExecuteAsync_KeepsConnectionRequired_WhenReactiveRefreshIsRejectedByTheProvider()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConfigureInstalledConnected(plugin, DateTime.UtcNow.AddMinutes(30));
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultDto.GrantRejected(
                "Google token endpoint returned 400 (invalid_grant)."));
        _gateway.ExecuteAsync(
                Arg.Any<PluginDefinitionDto>(),
                Arg.Any<McpToolDescriptorDto>(),
                Arg.Any<PluginConnection>(),
                Arg.Any<McpToolExecutionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new McpToolExecutionResult(false, PluginConstants.ErrorCodes.ConnectionRequired, "401", null, null, null));

        var result = await CreateSutWithRealRefresher().ExecuteAsync(UserId, Request("google_drive_search"));

        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.Value!.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Expired, connection.Status);
    }

    private McpToolOrchestrator CreateSutWithRealRefresher()
    {
        return new McpToolOrchestrator(
            _gateway,
            _unitOfWork,
            _workspacePolicy,
            new PluginConnectionService(_unitOfWork, _oauthClient, _stateProtector, _credentialProtector),
            _confirmationTokenService);
    }

    private McpToolOrchestrator CreateSut()
    {
        return new McpToolOrchestrator(_gateway, _unitOfWork, _workspacePolicy, _tokenRefresher, _confirmationTokenService);
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
