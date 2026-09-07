using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// Covers the operator-side lifecycle of a catalog row (WT-646).
/// </summary>
/// <remarks>
/// The assertion that matters most here is negative: <see cref="SecretPlaintext"/> and
/// <see cref="SecretCiphertext"/> must appear in no response payload from any endpoint. It is
/// asserted per endpoint rather than once, because "we do not return the secret" is a property of
/// every response shape and a new field on any DTO could break it silently.
/// </remarks>
public class PluginCatalogAdminServiceTests
{
    private const string SecretPlaintext = "super-secret-value";
    private const string SecretCiphertext = "enc:super-secret-value";
    private const string McpKey = "remote_app";
    private const string NativeKey = "google_drive";

    private static readonly Guid AdminUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid McpPluginId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid NativePluginId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid AuditUserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginRepository _pluginRepository = Substitute.For<IPluginRepository>();
    private readonly IPluginInstallationRepository _installationRepository = Substitute.For<IPluginInstallationRepository>();
    private readonly IPluginConnectionRepository _connectionRepository = Substitute.For<IPluginConnectionRepository>();
    private readonly IPluginToolAuditRepository _auditRepository = Substitute.For<IPluginToolAuditRepository>();
    private readonly IPluginCredentialProtector _credentialProtector = Substitute.For<IPluginCredentialProtector>();

    public PluginCatalogAdminServiceTests()
    {
        _unitOfWork.PluginRepository.Returns(_pluginRepository);
        _unitOfWork.PluginInstallationRepository.Returns(_installationRepository);
        _unitOfWork.PluginConnectionRepository.Returns(_connectionRepository);
        _unitOfWork.PluginToolAuditRepository.Returns(_auditRepository);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        _credentialProtector.Protect(Arg.Any<string>()).Returns(call => "enc:" + call.Arg<string>());
        // Unprotect is wired to throw rather than return a value: if any code path under test ever
        // decrypts a stored secret, the test that exercises it fails loudly instead of quietly
        // proving the wrong thing.
        _credentialProtector
            .When(protector => protector.Unprotect(Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("The admin catalog surface must never decrypt a client secret."));

        _installationRepository.CountByPluginAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _installationRepository.CountForPluginAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(0);
        _connectionRepository.CountForPluginAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(0);
    }

    // -----------------------------------------------------------------------------------------
    // Listing
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_IncludesRetiredRows()
    {
        // The user-facing catalog filters on is_active, which leaves a retired row invisible in the
        // one place someone would go to bring it back.
        var retired = McpPlugin();
        retired.IsActive = false;
        StubAllPlugins(retired, NativePlugin());

        var result = await CreateSut().ListAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value!, item => item.PluginKey == McpKey && !item.IsActive);
        Assert.Contains(result.Value!, item => item.PluginKey == NativeKey && item.IsActive);
    }

    [Fact]
    public async Task ListAsync_ReportsCredentialsAsBooleansAndLeaksNoSecret()
    {
        var plugin = McpPlugin();
        plugin.OAuthClientId = "client-abc";
        plugin.OAuthClientSecretEncrypted = SecretCiphertext;
        StubAllPlugins(plugin);

        var result = await CreateSut().ListAsync();

        var item = Assert.Single(result.Value!);
        Assert.True(item.HasClientId);
        Assert.True(item.HasClientSecret);
        AssertNoSecretIn(result.Value!);
    }

    [Fact]
    public async Task ListAsync_CountsToolsAndInstallations()
    {
        var plugin = McpPlugin();
        plugin.ToolsJson = JsonSerializer.Serialize(new[] { ToolJson("remote_app_search"), ToolJson("remote_app_create") });
        StubAllPlugins(plugin);
        _installationRepository.CountByPluginAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [McpPluginId] = 7 });

        var item = Assert.Single((await CreateSut().ListAsync()).Value!);

        Assert.Equal(2, item.ToolCount);
        Assert.Equal(7, item.InstallationCount);
    }

    [Fact]
    public async Task ListAsync_TreatsAnUnparseableManifestAsEmptyRatherThanFailingTheWholeListing()
    {
        // The admin surface is where someone goes to fix a row whose manifest is broken; throwing
        // on that row would take out the listing and leave no way in.
        var plugin = McpPlugin();
        plugin.ToolsJson = "{ not a tool array";
        StubAllPlugins(plugin);

        var result = await CreateSut().ListAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, Assert.Single(result.Value!).ToolCount);
    }

    [Fact]
    public async Task GetAsync_ReturnsUnknownPlugin_ForAKeyThatDoesNotExist()
    {
        StubLookup(null);

        var result = await CreateSut().GetAsync("nope");

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.UnknownPlugin, result.ErrorCode);
    }

    [Fact]
    public async Task GetAsync_ExposesTheClientIdButNeverTheSecret()
    {
        var plugin = McpPlugin();
        plugin.OAuthClientId = "client-abc";
        plugin.OAuthClientSecretEncrypted = SecretCiphertext;
        StubLookup(plugin);

        var result = await CreateSut().GetAsync(McpKey);

        // A client id is public by construction - it travels in every authorization URL the user's
        // own browser follows - so it is shown; a secret has no such justification.
        Assert.Equal("client-abc", result.Value!.OAuthClientId);
        Assert.True(result.Value!.HasClientSecret);
        AssertNoSecretIn(result.Value);
    }

    // -----------------------------------------------------------------------------------------
    // PATCH
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_ChangesOnlyTheSuppliedFields()
    {
        var plugin = McpPlugin();
        plugin.Label = "Original label";
        plugin.Description = "Original description";
        plugin.Category = "productivity";
        plugin.SortOrder = 3;
        plugin.IsFeatured = true;
        StubLookup(plugin);

        var result = await CreateSut().UpdateAsync(McpKey, new UpdatePluginCatalogRequest(Label: "New label"), AdminUserId);

        Assert.True(result.IsSuccess);
        Assert.Equal("New label", plugin.Label);
        Assert.Equal("Original description", plugin.Description);
        Assert.Equal("productivity", plugin.Category);
        Assert.Equal(3, plugin.SortOrder);
        Assert.True(plugin.IsFeatured);
    }

    [Fact]
    public async Task UpdateAsync_RecordsTheActingAdmin()
    {
        var plugin = McpPlugin();
        StubLookup(plugin);

        await CreateSut().UpdateAsync(McpKey, new UpdatePluginCatalogRequest(SortOrder: 9), AdminUserId);

        Assert.Equal(AdminUserId, plugin.UpdatedBy);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_LeavesUpdatedByNull_WhenTheTokenCarriedNoSubject()
    {
        // Guid.Empty is what User.GetUserId() degrades to. Recording it would assert an attribution
        // that is not true, and null already means "not a person" for every row a migration wrote.
        var plugin = McpPlugin();
        StubLookup(plugin);

        await CreateSut().UpdateAsync(McpKey, new UpdatePluginCatalogRequest(SortOrder: 9), Guid.Empty);

        Assert.Null(plugin.UpdatedBy);
    }

    [Fact]
    public async Task UpdateAsync_ClearsNullableColumnsWithAnEmptyString()
    {
        var plugin = McpPlugin();
        plugin.AvatarUrl = "https://example.test/icon.svg";
        plugin.Category = "productivity";
        StubLookup(plugin);

        await CreateSut().UpdateAsync(McpKey, new UpdatePluginCatalogRequest(AvatarUrl: "", Category: ""), AdminUserId);

        Assert.Null(plugin.AvatarUrl);
        Assert.Null(plugin.Category);
    }

    [Fact]
    public async Task UpdateAsync_TogglesIsActive_WhichIsHowARetiredRowComesBack()
    {
        var plugin = McpPlugin();
        plugin.IsActive = false;
        StubLookup(plugin);

        await CreateSut().UpdateAsync(McpKey, new UpdatePluginCatalogRequest(IsActive: true), AdminUserId);

        Assert.True(plugin.IsActive);
    }

    [Fact]
    public async Task UpdateAsync_RejectsABlankLabel_AndWritesNothingAtAll()
    {
        // The rejected field and an accepted field are sent together: a validator that assigned as
        // it went would leave the sortOrder staged on a tracked entity.
        var plugin = McpPlugin();
        plugin.Label = "Original label";
        plugin.SortOrder = 1;
        StubLookup(plugin);

        var result = await CreateSut().UpdateAsync(
            McpKey, new UpdatePluginCatalogRequest(Label: "   ", SortOrder: 42), AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.InvalidCatalogUpdate, result.ErrorCode);
        Assert.Equal("Original label", plugin.Label);
        Assert.Equal(1, plugin.SortOrder);
        Assert.Null(plugin.UpdatedBy);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    // plugins_mcp_requires_server_url would reject a cleared URL at the database.
    [InlineData("")]
    // Discovery and every MCP exchange run over TLS; http:// would be stored and then fail on use.
    [InlineData("http://insecure.test/mcp")]
    [InlineData("not-a-url")]
    public async Task UpdateAsync_RejectsAnUnusableMcpServerUrl(string serverUrl)
    {
        var plugin = McpPlugin();
        StubLookup(plugin);

        var result = await CreateSut().UpdateAsync(
            McpKey, new UpdatePluginCatalogRequest(McpServerUrl: serverUrl), AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.InvalidCatalogUpdate, result.ErrorCode);
    }

    [Fact]
    public async Task UpdateAsync_RefusesAnMcpServerUrlOnANativeRow()
    {
        // A native row is served by compiled-in code that never reads the column, so accepting one
        // would look like a fix and change nothing.
        var plugin = NativePlugin();
        StubLookup(plugin);

        var result = await CreateSut().UpdateAsync(
            NativeKey, new UpdatePluginCatalogRequest(McpServerUrl: "https://example.test/mcp"), AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Null(plugin.McpServerUrl);
    }

    [Fact]
    public async Task UpdateAsync_DeduplicatesRequiredScopes()
    {
        var plugin = McpPlugin();
        StubLookup(plugin);

        await CreateSut().UpdateAsync(
            McpKey,
            new UpdatePluginCatalogRequest(RequiredScopes: ["scope.read", "scope.read", " scope.write "]),
            AdminUserId);

        Assert.Equal(["scope.read", "scope.write"], JsonSerializer.Deserialize<string[]>(plugin.RequiredScopesJson)!);
    }

    // -----------------------------------------------------------------------------------------
    // PUT /oauth
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task SetOAuthClientAsync_EncryptsTheSecretAndNeverReturnsIt()
    {
        var plugin = McpPlugin();
        StubLookup(plugin);

        var result = await CreateSut().SetOAuthClientAsync(
            McpKey, new SetPluginOAuthClientRequest("client-abc", SecretPlaintext), AdminUserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.OAuthClientSource.Preregistered, plugin.OAuthClientSource);
        Assert.Equal("client-abc", plugin.OAuthClientId);
        // Stored encrypted, exactly as CreateMcpPluginAsync does it - never in the clear.
        Assert.Equal(SecretCiphertext, plugin.OAuthClientSecretEncrypted);
        _credentialProtector.Received(1).Protect(SecretPlaintext);
        Assert.True(result.Value!.HasClientSecret);
        AssertNoSecretIn(result.Value);
    }

    [Fact]
    public async Task SetOAuthClientAsync_RotatingTheIdAloneLeavesTheStoredSecretAlone()
    {
        // No endpoint hands a secret back, so an operator rotating only the id has no way to
        // re-send one. Treating an omitted secret as "clear it" would break every such rotation.
        var plugin = McpPlugin();
        plugin.OAuthClientSecretEncrypted = SecretCiphertext;
        StubLookup(plugin);

        await CreateSut().SetOAuthClientAsync(McpKey, new SetPluginOAuthClientRequest("client-rotated"), AdminUserId);

        Assert.Equal("client-rotated", plugin.OAuthClientId);
        Assert.Equal(SecretCiphertext, plugin.OAuthClientSecretEncrypted);
    }

    [Fact]
    public async Task SetOAuthClientAsync_ClearsTheSecretOnAnEmptyString()
    {
        var plugin = McpPlugin();
        plugin.OAuthClientSecretEncrypted = SecretCiphertext;
        StubLookup(plugin);

        var result = await CreateSut().SetOAuthClientAsync(
            McpKey, new SetPluginOAuthClientRequest("client-abc", ""), AdminUserId);

        Assert.Null(plugin.OAuthClientSecretEncrypted);
        Assert.False(result.Value!.HasClientSecret);
        Assert.Null(result.Value!.CredentialsUpdatedAt);
    }

    [Fact]
    public async Task SetOAuthClientAsync_RequiresAClientId()
    {
        // plugins_preregistered_requires_client_id makes a blank id unstorable at this source.
        var plugin = McpPlugin();
        StubLookup(plugin);

        var result = await CreateSut().SetOAuthClientAsync(McpKey, new SetPluginOAuthClientRequest("  "), AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.InvalidCatalogUpdate, result.ErrorCode);
        Assert.Equal(PluginConstants.OAuthClientSource.Unresolved, plugin.OAuthClientSource);
    }

    [Fact]
    public async Task SetOAuthClientAsync_RefusesANativeRow()
    {
        // A native row's client comes from service configuration. Writing one here would be inert,
        // and an operator chasing an empty client id in production would believe it was fixed.
        var plugin = NativePlugin();
        StubLookup(plugin);

        var result = await CreateSut().SetOAuthClientAsync(
            NativeKey, new SetPluginOAuthClientRequest("client-abc", SecretPlaintext), AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Null(plugin.OAuthClientId);
        Assert.Null(plugin.OAuthClientSecretEncrypted);
        _credentialProtector.DidNotReceive().Protect(Arg.Any<string>());
    }

    // -----------------------------------------------------------------------------------------
    // PUT /tools
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ReplaceToolsAsync_WritesTheManifestAndStampsTheRouteKeyOntoEveryTool()
    {
        var plugin = McpPlugin();
        StubLookup(plugin);

        var result = await CreateSut().ReplaceToolsAsync(
            McpKey, new ReplacePluginToolsRequest([ValidTool("remote_app_search")]), AdminUserId);

        Assert.True(result.IsSuccess);
        var tool = Assert.Single(result.Value!.Tools);
        Assert.Equal("remote_app_search", tool.Name);
        // The submitted entry carries no pluginKey at all: the orchestrator resolves a tool call by
        // that field, so it is stamped from the route rather than trusted from the body.
        Assert.Equal(McpKey, tool.PluginKey);
        Assert.Contains("remote_app_search", plugin.ToolsJson);
    }

    [Fact]
    public async Task ReplaceToolsAsync_ClearsTheSyncMarkers()
    {
        // tools_synced_at means "this is what the MCP server last told us". A hand-authored manifest
        // is not that, and leaving the marker would let an operator edit masquerade as a successful
        // tools/list.
        var plugin = McpPlugin();
        plugin.ToolsSyncedAt = DateTime.UtcNow.AddDays(-1);
        plugin.ToolsManifestHash = "deadbeef";
        StubLookup(plugin);

        await CreateSut().ReplaceToolsAsync(McpKey, new ReplacePluginToolsRequest([ValidTool("remote_app_search")]), AdminUserId);

        Assert.Null(plugin.ToolsSyncedAt);
        Assert.Null(plugin.ToolsManifestHash);
    }

    [Fact]
    public async Task ReplaceToolsAsync_AcceptsAnEmptyManifest()
    {
        // The correct state for a fresh kind='mcp' row whose tools arrive from tools/list.
        var plugin = McpPlugin();
        plugin.ToolsJson = JsonSerializer.Serialize(new[] { ToolJson("remote_app_search") });
        StubLookup(plugin);

        var result = await CreateSut().ReplaceToolsAsync(McpKey, new ReplacePluginToolsRequest([]), AdminUserId);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Tools);
    }

    [Fact]
    public async Task ReplaceToolsAsync_RejectsAMissingToolsArray()
    {
        // Clearing is spelled "tools": [], which cannot be typed by accident. A body with no
        // property at all is a mistake.
        var plugin = McpPlugin();
        StubLookup(plugin);

        var result = await CreateSut().ReplaceToolsAsync(McpKey, new ReplacePluginToolsRequest(null), AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.InvalidToolManifest, result.ErrorCode);
    }

    /// <summary>
    /// Builds one malformed manifest entry per named defect.
    /// </summary>
    /// <remarks>
    /// The cases are named by string and built here rather than passed as objects through
    /// <c>TheoryData</c>, so every theory argument stays serialisable and the test names read as the
    /// defect being rejected.
    /// </remarks>
    private static PluginToolManifestEntryDto MalformedTool(string defect) => defect switch
    {
        "blank name" => ValidTool("x") with { Name = "  " },
        "illegal characters in the name" => ValidTool("x") with { Name = "has spaces" },
        "no label" => ValidTool("x") with { Label = "" },
        "no description" => ValidTool("x") with { Description = "" },
        "no effect" => ValidTool("x") with { Effect = null },
        // 'write' is what makes a call require a confirmation token, so an unrecognised effect would
        // leave a side-effecting tool running with no confirmation at all.
        "an effect outside the contract" => ValidTool("x") with { Effect = "delete" },
        "no requiredScopes property" => ValidTool("x") with { RequiredScopes = null },
        "a blank scope" => ValidTool("x") with { RequiredScopes = ["  "] },
        "no parameters schema" => ValidTool("x") with { Parameters = null },
        "parameters that are not an object schema" => ValidTool("x") with { Parameters = new JsonObject { ["type"] = "string" } },
        // GetValue<string>() throws on a number; a manifest holding "type": 3 has to come back as a
        // validation error rather than an exception.
        "a non-string parameters type" => ValidTool("x") with { Parameters = new JsonObject { ["type"] = 3 } },
        "no properties map" => ValidTool("x") with { Parameters = new JsonObject { ["type"] = "object" } },
        // The costliest manifest bug to find: the model is told an argument is mandatory and given
        // no schema for it, so it invents one and the gateway rejects the call.
        "a required argument that is not declared" => ValidTool("x") with
        {
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("limit"),
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, "Unknown manifest defect."),
    };

    [Theory]
    [InlineData("blank name")]
    [InlineData("illegal characters in the name")]
    [InlineData("no label")]
    [InlineData("no description")]
    [InlineData("no effect")]
    [InlineData("an effect outside the contract")]
    [InlineData("no requiredScopes property")]
    [InlineData("a blank scope")]
    [InlineData("no parameters schema")]
    [InlineData("parameters that are not an object schema")]
    [InlineData("a non-string parameters type")]
    [InlineData("no properties map")]
    [InlineData("a required argument that is not declared")]
    public async Task ReplaceToolsAsync_RejectsAMalformedManifest(string defect)
    {
        var tool = MalformedTool(defect);
        var plugin = McpPlugin();
        var originalTools = plugin.ToolsJson;
        StubLookup(plugin);

        var result = await CreateSut().ReplaceToolsAsync(McpKey, new ReplacePluginToolsRequest([tool]), AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.InvalidToolManifest, result.ErrorCode);
        Assert.Equal(originalTools, plugin.ToolsJson);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceToolsAsync_RejectsDuplicateToolNames()
    {
        // Even where the orchestrator's Ordinal match would resolve them, the model is left choosing
        // between two entries it cannot tell apart.
        var plugin = McpPlugin();
        StubLookup(plugin);

        var result = await CreateSut().ReplaceToolsAsync(
            McpKey,
            new ReplacePluginToolsRequest([ValidTool("remote_app_search"), ValidTool("Remote_App_Search")]),
            AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Contains("duplicate", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplaceToolsAsync_ReportsEveryProblemAtOnce()
    {
        // An operator editing a manifest by hand wants one round trip, not one per typo.
        var plugin = McpPlugin();
        StubLookup(plugin);

        var result = await CreateSut().ReplaceToolsAsync(
            McpKey,
            new ReplacePluginToolsRequest([ValidTool("ok_tool") with { Name = "", Label = "", Effect = "sideways" }]),
            AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Contains("'name'", result.Error!);
        Assert.Contains("'label'", result.Error!);
        Assert.Contains("'effect'", result.Error!);
    }

    // -----------------------------------------------------------------------------------------
    // Rediscover
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RediscoverAsync_ClearsCachedDiscoveryButKeepsAPreregisteredClient()
    {
        // A pre-registered client is an operator's own registration; it did not come from discovery,
        // and dropping it would turn "re-read the server" into "go and register the app again".
        var plugin = McpPlugin();
        plugin.OAuthClientSource = PluginConstants.OAuthClientSource.Preregistered;
        plugin.OAuthClientId = "client-abc";
        plugin.OAuthClientSecretEncrypted = SecretCiphertext;
        plugin.OAuthAuthorizationEndpoint = "https://old.test/authorize";
        plugin.OAuthTokenEndpoint = "https://old.test/token";
        plugin.OAuthRevokeEndpoint = "https://old.test/revoke";
        plugin.OAuthRegistrationEndpoint = "https://old.test/register";
        plugin.OAuthCimdSupported = true;
        plugin.OAuthIssParameterSupported = true;
        plugin.OAuthTokenEndpointAuthMethod = PluginConstants.TokenEndpointAuthMethod.ClientSecretPost;
        StubLookup(plugin);

        var result = await CreateSut().RediscoverAsync(McpKey, AdminUserId);

        Assert.True(result.IsSuccess);
        Assert.Null(plugin.OAuthAuthorizationEndpoint);
        Assert.Null(plugin.OAuthTokenEndpoint);
        Assert.Null(plugin.OAuthRevokeEndpoint);
        Assert.Null(plugin.OAuthRegistrationEndpoint);
        Assert.Null(plugin.OAuthCimdSupported);
        Assert.Null(plugin.OAuthIssParameterSupported);
        Assert.Null(plugin.OAuthTokenEndpointAuthMethod);
        Assert.Equal("client-abc", plugin.OAuthClientId);
        Assert.Equal(PluginConstants.OAuthClientSource.Preregistered, plugin.OAuthClientSource);
        AssertNoSecretIn(result.Value!);
    }

    [Theory]
    [InlineData(PluginConstants.OAuthClientSource.Cimd)]
    [InlineData(PluginConstants.OAuthClientSource.Dcr)]
    public async Task RediscoverAsync_DropsAClientThatDiscoveryItselfIssued(string source)
    {
        // A cimd or dcr client identifies us to the endpoints being cleared, so it goes with them
        // and the ladder starts over.
        var plugin = McpPlugin();
        plugin.OAuthClientSource = source;
        plugin.OAuthClientId = "issued-by-discovery";
        StubLookup(plugin);

        await CreateSut().RediscoverAsync(McpKey, AdminUserId);

        Assert.Null(plugin.OAuthClientId);
        Assert.Equal(PluginConstants.OAuthClientSource.Unresolved, plugin.OAuthClientSource);
    }

    [Fact]
    public async Task RediscoverAsync_RefusesANativeRow()
    {
        var plugin = NativePlugin();
        plugin.OAuthTokenEndpoint = "https://oauth2.googleapis.com/token";
        StubLookup(plugin);

        var result = await CreateSut().RediscoverAsync(NativeKey, AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal("https://oauth2.googleapis.com/token", plugin.OAuthTokenEndpoint);
    }

    // -----------------------------------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_SoftDeleteRetiresTheRowAndKeepsIt()
    {
        var plugin = McpPlugin();
        StubLookup(plugin);
        _installationRepository.CountForPluginAsync(McpPluginId, Arg.Any<CancellationToken>()).Returns(4);

        var result = await CreateSut().DeleteAsync(McpKey, hard: false, AdminUserId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.HardDeleted);
        Assert.False(plugin.IsActive);
        Assert.Equal(AdminUserId, plugin.UpdatedBy);
        _pluginRepository.DidNotReceive().Remove(Arg.Any<Plugin>());
    }

    [Fact]
    public async Task DeleteAsync_RefusesAHardDeleteWhileInstallationsExist()
    {
        var plugin = McpPlugin();
        StubLookup(plugin);
        _installationRepository.CountForPluginAsync(McpPluginId, Arg.Any<CancellationToken>()).Returns(2);

        var result = await CreateSut().DeleteAsync(McpKey, hard: true, AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PluginInUse, result.ErrorCode);
        _pluginRepository.DidNotReceive().Remove(Arg.Any<Plugin>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_RefusesAHardDeleteWhileConnectionsExist()
    {
        // plugin_connections_plugin_id_fkey is ON DELETE RESTRICT, so letting this through would
        // answer with a Postgres constraint name out of SaveChangesAsync.
        var plugin = McpPlugin();
        StubLookup(plugin);
        _connectionRepository.CountForPluginAsync(McpPluginId, Arg.Any<CancellationToken>()).Returns(1);

        var result = await CreateSut().DeleteAsync(McpKey, hard: true, AdminUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PluginInUse, result.ErrorCode);
        Assert.Contains("1 connection", result.Error!);
        _pluginRepository.DidNotReceive().Remove(Arg.Any<Plugin>());
    }

    [Fact]
    public async Task DeleteAsync_HardDeletesARowNothingReferences()
    {
        var plugin = McpPlugin();
        StubLookup(plugin);

        var result = await CreateSut().DeleteAsync(McpKey, hard: true, AdminUserId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.HardDeleted);
        _pluginRepository.Received(1).Remove(plugin);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------------------------
    // Audits
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ListAuditsAsync_TurnsAPageNumberIntoASkipAndEchoesTheTotal()
    {
        var plugin = McpPlugin();
        StubLookup(plugin);
        StubAudits(97, Audit("remote_app_search", "ok"));

        var result = await CreateSut().ListAuditsAsync(McpKey, new PluginToolAuditQueryDto(Page: 3, PageSize: 25));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.Page);
        Assert.Equal(25, result.Value!.PageSize);
        Assert.Equal(97, result.Value!.TotalCount);
        await _auditRepository.Received(1).ListForPluginAsync(
            McpPluginId, null, null, 50, 25, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAuditsAsync_PassesTheUserAndOutcomeFiltersThrough()
    {
        var plugin = McpPlugin();
        StubLookup(plugin);
        StubAudits(1, Audit("remote_app_search", PluginConstants.ErrorCodes.MissingScope));

        await CreateSut().ListAuditsAsync(
            McpKey,
            new PluginToolAuditQueryDto(AuditUserId, "  " + PluginConstants.ErrorCodes.MissingScope + "  "));

        await _auditRepository.Received(1).ListForPluginAsync(
            McpPluginId, AuditUserId, PluginConstants.ErrorCodes.MissingScope, 0, 50, Arg.Any<CancellationToken>());
    }

    [Theory]
    // A page size out of range is a caller bug worth absorbing; an unbounded one is a
    // denial-of-service knob on a table with no retention policy.
    [InlineData(0, 50)]
    [InlineData(-4, 50)]
    [InlineData(5000, 200)]
    public async Task ListAuditsAsync_NormalisesPagingRatherThanRejectingIt(int pageSize, int expectedPageSize)
    {
        var plugin = McpPlugin();
        StubLookup(plugin);
        StubAudits(0);

        var result = await CreateSut().ListAuditsAsync(McpKey, new PluginToolAuditQueryDto(Page: -1, PageSize: pageSize));

        Assert.Equal(1, result.Value!.Page);
        Assert.Equal(expectedPageSize, result.Value!.PageSize);
        await _auditRepository.Received(1).ListForPluginAsync(
            McpPluginId, null, null, 0, expectedPageSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAuditsAsync_ReturnsUnknownPlugin_ForAKeyThatDoesNotExist()
    {
        StubLookup(null);

        var result = await CreateSut().ListAuditsAsync("nope", new PluginToolAuditQueryDto());

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.UnknownPlugin, result.ErrorCode);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private PluginCatalogAdminService CreateSut() => new(_unitOfWork, _credentialProtector);

    /// <summary>
    /// Serialises a payload exactly as the API would and asserts the secret is nowhere in it, in
    /// plaintext or ciphertext. Cheap, and it catches a leak introduced by a new DTO field that no
    /// property-by-property assertion would think to look at.
    /// </summary>
    private static void AssertNoSecretIn(object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(SecretPlaintext, json, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretCiphertext, json, StringComparison.Ordinal);
    }

    private void StubLookup(Plugin? plugin) =>
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);

    private void StubAllPlugins(params Plugin[] plugins) =>
        _pluginRepository.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(plugins);

    private void StubAudits(int totalCount, params PluginToolAudit[] audits) =>
        _auditRepository.ListForPluginAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid?>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<PluginToolAudit>)audits, totalCount));

    private static Plugin McpPlugin() => new()
    {
        Id = McpPluginId,
        PluginKey = McpKey,
        Label = "Remote app",
        Description = "A remote MCP server.",
        Provider = McpKey,
        Kind = PluginConstants.PluginKind.Mcp,
        McpServerUrl = "https://example.test/mcp",
        RequiredScopesJson = "[]",
        ToolsJson = "[]",
        OAuthClientSource = PluginConstants.OAuthClientSource.Unresolved,
        IsActive = true,
        CreatedAt = DateTime.UtcNow.AddDays(-10),
        UpdatedAt = DateTime.UtcNow.AddDays(-10),
    };

    private static Plugin NativePlugin() => new()
    {
        Id = NativePluginId,
        PluginKey = NativeKey,
        Label = "Google Drive",
        Description = "Search files in Google Drive.",
        Provider = PluginConstants.Providers.Google,
        Kind = PluginConstants.PluginKind.Native,
        RequiredScopesJson = "[]",
        ToolsJson = "[]",
        OAuthClientSource = PluginConstants.OAuthClientSource.Unresolved,
        IsActive = true,
        CreatedAt = DateTime.UtcNow.AddDays(-10),
        UpdatedAt = DateTime.UtcNow.AddDays(-10),
    };

    private static PluginToolAudit Audit(string toolName, string resultStatus) => new()
    {
        Id = Guid.NewGuid(),
        UserId = AuditUserId,
        PluginId = McpPluginId,
        PluginKey = McpKey,
        ToolName = toolName,
        ResultStatus = resultStatus,
        CreatedAt = DateTime.UtcNow,
    };

    private static PluginToolManifestEntryDto ValidTool(string name) => new(
        name,
        "Search the remote app",
        "Search the connected remote app for matching records.",
        PluginConstants.ToolEffect.Read,
        ["remote.read"],
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("query"),
        });

    private static object ToolJson(string name) => new
    {
        name,
        pluginKey = McpKey,
        label = "Search",
        description = "Search.",
        effect = PluginConstants.ToolEffect.Read,
        requiredScopes = new[] { "remote.read" },
        parameters = new { type = "object", properties = new { } },
    };
}
