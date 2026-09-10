using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Infrastructure.Mcp;
using WarpTalk.AssistantService.Infrastructure.OAuth;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// Drives discovery and the registration ladder against a real HTTP server.
/// </summary>
/// <remarks>
/// The unit tests elsewhere substitute the metadata, which means they verify the ladder's decisions
/// but never that we can actually find the documents those decisions are made from. That gap is
/// where the reference clients' bugs live: the well-known probing order, the <c>WWW-Authenticate</c>
/// route, and the PKCE refusal are all things that only fail against a server.
/// <para>
/// A loopback <see cref="HttpListener"/> is used rather than a container so this runs in CI without
/// Docker. It answers the same documents a Cloudflare-backed MCP server does.
/// </para>
/// </remarks>
public class McpDiscoveryAndLadderIntegrationTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _origin = null!;
    private CancellationTokenSource _cts = null!;
    private Task _loop = null!;

    /// <summary>Flipped per test to model what a given authorization server advertises.</summary>
    private bool _advertiseCimd = true;
    // Explicitly false rather than left to the default: no test in this class currently flips it,
    // so without an initializer the compiler reports CS0649 and --warnaserror fails the build.
    // The value is load-bearing (read where the registration endpoint is advertised), and a server
    // that offers CIMD is exactly the case where DCR should not be reached.
    private bool _advertiseRegistration = false;
    private bool _advertisePkce = true;
    private bool _answerWwwAuthenticate;
    private bool _publishProtectedResourceMetadata = true;
    private string[] _tokenEndpointAuthMethods = ["client_secret_basic", "client_secret_post", "none"];

    public Task InitializeAsync()
    {
        // A free loopback port, claimed by trying until one binds.
        for (var port = 21500; port < 21600; port++)
        {
            var prefix = $"http://localhost:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                _listener = listener;
                _origin = $"http://localhost:{port}";
                break;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        Assert.NotNull(_listener);
        _cts = new CancellationTokenSource();
        _loop = Task.Run(ServeAsync);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cts.Cancel();
        _listener.Close();
        return Task.CompletedTask;
    }

    // ---- the fake authorization server + protected resource ----------------------------------

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var path = context.Request.Url!.AbsolutePath;

            switch (path)
            {
                case "/mcp" when _answerWwwAuthenticate:
                    context.Response.StatusCode = 401;
                    context.Response.AddHeader(
                        "WWW-Authenticate",
                        $"Bearer resource_metadata=\"{_origin}/.well-known/oauth-protected-resource\", scope=\"tools:read\"");
                    Write(context, "{}");
                    break;

                case "/.well-known/oauth-protected-resource" when _publishProtectedResourceMetadata:
                    Write(context, $$"""
                        {
                          "resource": "{{_origin}}/mcp",
                          "authorization_servers": ["{{_origin}}"],
                          "scopes_supported": ["tools:read", "tools:write"]
                        }
                        """);
                    break;

                case "/.well-known/oauth-authorization-server":
                    Write(context, BuildAuthorizationServerMetadata());
                    break;

                default:
                    context.Response.StatusCode = 404;
                    Write(context, "{}");
                    break;
            }
        }
    }

    private string BuildAuthorizationServerMetadata()
    {
        var methods = string.Join(", ", _tokenEndpointAuthMethods.Select(m => $"\"{m}\""));
        var pkce = _advertisePkce
            ? "\"code_challenge_methods_supported\": [\"S256\"],"
            : string.Empty;
        var cimd = _advertiseCimd ? "\"client_id_metadata_document_supported\": true," : "";
        var registration = _advertiseRegistration
            ? $"\"registration_endpoint\": \"{_origin}/register\","
            : "";

        return $$"""
            {
              "issuer": "{{_origin}}",
              "authorization_endpoint": "{{_origin}}/authorize",
              "token_endpoint": "{{_origin}}/token",
              "revocation_endpoint": "{{_origin}}/revoke",
              {{registration}}
              {{cimd}}
              "authorization_response_iss_parameter_supported": true,
              {{pkce}}
              "token_endpoint_auth_methods_supported": [{{methods}}],
              "scopes_supported": ["tools:read", "tools:write"]
            }
            """;
    }

    private static void Write(HttpListenerContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.OutputStream.Close();
    }

    // ---- subjects under test -----------------------------------------------------------------

    private Plugin McpPlugin() => new()
    {
        Id = Guid.NewGuid(),
        PluginKey = "remote_app",
        Label = "Remote App",
        Description = "A real MCP server.",
        Provider = "remote_app",
        Kind = PluginConstants.PluginKind.Mcp,
        McpServerUrl = $"{_origin}/mcp",
        OAuthClientSource = PluginConstants.OAuthClientSource.Unresolved,
        RequiredScopesJson = "[]",
        ToolsJson = "[]",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static McpAuthorizationServerDiscovery Discovery() =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

    private CimdClientRegistrar Cimd(string metadataUrl = "https://warptalk.test/oauth/client-metadata/v1.json") =>
        new(Options.Create(new McpClientOptions { ClientMetadataUrl = metadataUrl }));

    // ---- discovery -----------------------------------------------------------------------------

    [Fact]
    public async Task Discovery_FindsTheAuthorizationServer_ThroughTheWellKnownProbe()
    {
        var result = await Discovery().DiscoverAsync(McpPlugin());

        Assert.True(result.IsSuccess, result.Error);
        var discovery = result.Value!;

        // The RFC 8707 resource must be the canonical server URI, since the identical string has to
        // go on both the authorization and token requests.
        Assert.Equal($"{_origin}/mcp", discovery.ResourceIdentifier);
        Assert.Equal(["tools:read", "tools:write"], discovery.ResourceScopesSupported);
        Assert.Equal($"{_origin}/authorize", discovery.AuthorizationServer.AuthorizationEndpoint);
        Assert.Equal($"{_origin}/token", discovery.AuthorizationServer.TokenEndpoint);
        Assert.True(discovery.AuthorizationServer.ClientIdMetadataDocumentSupported);
        Assert.True(discovery.AuthorizationServer.IssParameterSupported);
    }

    [Fact]
    public async Task Discovery_PrefersTheResourceMetadataUrlFromTheWwwAuthenticateHeader()
    {
        _answerWwwAuthenticate = true;

        var result = await Discovery().DiscoverAsync(McpPlugin());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal($"{_origin}/authorize", result.Value!.AuthorizationServer.AuthorizationEndpoint);
    }

    [Fact]
    public async Task Discovery_RefusesToProceed_WhenTheServerDoesNotAdvertisePkce()
    {
        // MCP Authorization makes verifying PKCE support a client MUST. Failing loudly here is much
        // cheaper to diagnose than the unrelated-looking token failure it otherwise becomes -
        // exactly the trap AWS Cognito sets for clients that skip the check.
        _advertisePkce = false;

        var result = await Discovery().DiscoverAsync(McpPlugin());

        Assert.False(result.IsSuccess);
        Assert.Contains("PKCE", result.Error);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task Discovery_FallsBackToTheServerOrigin_WhenThereIsNoProtectedResourceMetadata()
    {
        // A server built against MCP Authorization 2025-03-26 publishes only RFC 8414 metadata at
        // its own origin and no RFC 9728 document (Atlassian's hosted server does this today).
        // The spec keeps that as the backwards-compatibility path, so discovery must treat the
        // server itself as the authorization server rather than refuse.
        _publishProtectedResourceMetadata = false;

        var result = await Discovery().DiscoverAsync(McpPlugin());

        Assert.True(result.IsSuccess, result.Error);
        var discovery = result.Value!;
        Assert.Equal($"{_origin}/mcp", discovery.ResourceIdentifier);
        Assert.Equal($"{_origin}/authorize", discovery.AuthorizationServer.AuthorizationEndpoint);
        Assert.Equal($"{_origin}/token", discovery.AuthorizationServer.TokenEndpoint);
        // Nothing to read scopes from, so the resource advertises none.
        Assert.Empty(discovery.ResourceScopesSupported);
    }

    [Fact]
    public async Task Discovery_Fails_WhenTheServerPublishesNothing()
    {
        var plugin = McpPlugin();
        plugin.McpServerUrl = "http://localhost:21499/mcp";

        var result = await Discovery().DiscoverAsync(plugin);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.ErrorCode);
    }

    // ---- the ladder, on real discovered metadata ---------------------------------------------

    [Fact]
    public async Task Cimd_IsChosen_AndNegotiatesNone_AgainstADefaultConfiguredServer()
    {
        // The default Workers OAuth provider accepts client_secret_basic, client_secret_post and
        // none. After the CIMD filter strips the shared-secret methods only 'none' survives, which
        // is precisely why advertising private_key_jwt alone would be a hard failure here.
        var metadata = (await Discovery().DiscoverAsync(McpPlugin())).Value!.AuthorizationServer;
        var registrar = Cimd();

        Assert.True(registrar.CanResolve(McpPlugin(), metadata));

        var identity = await registrar.ResolveAsync(McpPlugin(), metadata);

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal("https://warptalk.test/oauth/client-metadata/v1.json", identity.ClientId);
        Assert.Equal(PluginConstants.OAuthClientSource.Cimd, identity.Source);
        Assert.Equal(PluginConstants.TokenEndpointAuthMethod.None, identity.TokenEndpointAuthMethod);

        // A metadata-document client can never hold a shared secret.
        Assert.Null(identity.EncryptedClientSecret);
    }

    [Fact]
    public async Task Cimd_NegotiatesPrivateKeyJwt_WhenTheServerAcceptsIt()
    {
        _tokenEndpointAuthMethods = ["client_secret_basic", "private_key_jwt", "none"];

        var metadata = (await Discovery().DiscoverAsync(McpPlugin())).Value!.AuthorizationServer;

        var identity = await Cimd().ResolveAsync(McpPlugin(), metadata);

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal(PluginConstants.TokenEndpointAuthMethod.PrivateKeyJwt, identity.TokenEndpointAuthMethod);
    }

    [Fact]
    public async Task Cimd_Declines_WhenTheServerDoesNotAdvertiseIt()
    {
        _advertiseCimd = false;

        var metadata = (await Discovery().DiscoverAsync(McpPlugin())).Value!.AuthorizationServer;
        var registrar = Cimd();

        Assert.False(registrar.CanResolve(McpPlugin(), metadata));

        // Declining is a return value, never an exception: falling through a rung is ordinary
        // control flow, and throwing here is the bug that makes the reference clients unusable.
        var identity = await registrar.ResolveAsync(McpPlugin(), metadata);
        Assert.Equal(McpClientRegistrationOutcome.Unsupported, identity.Outcome);
    }

    [Fact]
    public async Task Cimd_Declines_WhenOurOwnMetadataUrlIsNotConfigured()
    {
        // The correct configuration until the document is reachable on a public HTTPS host: a
        // provider has to fetch it from the open internet, so a blank URL disables the rung rather
        // than advertising something unfetchable.
        var metadata = (await Discovery().DiscoverAsync(McpPlugin())).Value!.AuthorizationServer;

        var identity = await Cimd(metadataUrl: string.Empty).ResolveAsync(McpPlugin(), metadata);

        Assert.Equal(McpClientRegistrationOutcome.Unsupported, identity.Outcome);
        Assert.Contains("ClientMetadataUrl", identity.Detail);
    }

    // ---- the rung has to survive the step that comes after it ---------------------------------

    /// <summary>
    /// The whole point of resolving a client identity is the request that follows it, and until
    /// this test existed nothing crossed that seam: <c>CimdClientRegistrar</c> was covered in
    /// isolation, and the authorization URL was only ever built from a row a preregistered or DCR
    /// rung had filled in.
    /// </summary>
    /// <remarks>
    /// The gap was live. <see cref="McpClientRegistrationMapper.ApplyClientIdentity"/> stores null
    /// for a CIMD client on purpose - the id is our metadata URL, which is configuration, not row
    /// state - and <see cref="McpOAuthClient"/> then read the client id straight off the row. So
    /// the rung resolved, the row was written exactly as designed, and the very next call threw
    /// <c>PluginNotConfiguredException</c> on the null it had just been handed: a 500 on Connect
    /// for every server that advertises CIMD, and only for those. Local deployments never saw it
    /// because a blank ClientMetadataUrl disables the rung and everything falls through to DCR.
    /// </remarks>
    [Fact]
    public async Task Cimd_ProducesAnAuthorizationUrl_CarryingOurMetadataUrlAsTheClientId()
    {
        const string metadataUrl = "https://warptalk.test/oauth/client-metadata/v1.json";

        var metadata = (await Discovery().DiscoverAsync(McpPlugin())).Value!.AuthorizationServer;
        var identity = await Cimd(metadataUrl).ResolveAsync(McpPlugin(), metadata);
        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);

        // Exactly what a connect does: cache discovery, then record the identity, then build.
        var plugin = McpPlugin();
        McpClientRegistrationMapper.ApplyDiscovery(plugin, (await Discovery().DiscoverAsync(plugin)).Value!);
        McpClientRegistrationMapper.ApplyClientIdentity(plugin, identity);

        // The row is null here, and that is correct - the assertion guards the invariant this fix
        // had to preserve rather than trade away.
        Assert.Null(plugin.OAuthClientId);
        Assert.Equal(PluginConstants.OAuthClientSource.Cimd, plugin.OAuthClientSource);

        var oauthClient = OAuthClient(metadataUrl);
        var flowState = oauthClient.PrepareState(plugin, new PluginOAuthStateDto(Guid.NewGuid(), plugin.PluginKey));

        var url = oauthClient.BuildAuthorizationUrl(plugin, ["mcp:read"], "sealed-state", flowState);

        // Decoded rather than substring-matched: the client id is a URL inside a query string, and
        // asserting on one particular percent-encoding would fail the day the builder changes
        // without the request being any different to the server that receives it.
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal(metadataUrl, query["client_id"]);
    }

    /// <summary>
    /// The same resolution has to survive the token request too, which is a separate seam: the
    /// exchange and the refresh both load the row and call this client without ever running the
    /// ladder, so a fix that only reached the authorization URL would move the 500 one step later.
    /// </summary>
    [Fact]
    public async Task Cimd_AuthenticatesTheTokenRequest_AsAPublicClientWithOurMetadataUrl()
    {
        const string metadataUrl = "https://warptalk.test/oauth/client-metadata/v1.json";

        var metadata = (await Discovery().DiscoverAsync(McpPlugin())).Value!.AuthorizationServer;
        var identity = await Cimd(metadataUrl).ResolveAsync(McpPlugin(), metadata);

        var plugin = McpPlugin();
        McpClientRegistrationMapper.ApplyDiscovery(plugin, (await Discovery().DiscoverAsync(plugin)).Value!);
        McpClientRegistrationMapper.ApplyClientIdentity(plugin, identity);

        // A default-configured server leaves 'none' as the only method a document client can use,
        // so the token request carries client_id in the form and no Authorization header.
        Assert.Equal(PluginConstants.TokenEndpointAuthMethod.None, plugin.OAuthTokenEndpointAuthMethod);

        var form = new Dictionary<string, string>();
        var applied = ApplyClientAuthentication(OAuthClient(metadataUrl), plugin, form);

        Assert.Null(applied);
        Assert.Equal(metadataUrl, form["client_id"]);
    }

    /// <summary>
    /// Reaches the private seam both public entry points share, so the two tests above assert the
    /// same resolution rather than two coincidentally-agreeing implementations.
    /// </summary>
    private static System.Net.Http.Headers.AuthenticationHeaderValue? ApplyClientAuthentication(
        McpOAuthClient client,
        Plugin plugin,
        Dictionary<string, string> form)
    {
        var method = typeof(McpOAuthClient).GetMethod(
            "ApplyClientAuthentication",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var args = new object?[] { plugin, form, null };
        method.Invoke(client, args);
        return (System.Net.Http.Headers.AuthenticationHeaderValue?)args[2];
    }

    private static McpOAuthClient OAuthClient(string metadataUrl)
    {
        var options = Options.Create(new McpClientOptions
        {
            ClientMetadataUrl = metadataUrl,
            RedirectUri = "https://warptalk.test/api/v1/assistant/plugins/mcp/oauth/callback",
        });

        return new McpOAuthClient(
            new HttpClient(),
            options,
            new ConfigurationMcpClientSigningKeyStore(options, NullLogger<ConfigurationMcpClientSigningKeyStore>.Instance),
            Substitute.For<IPluginCredentialProtector>(),
            NullLogger<McpOAuthClient>.Instance);
    }

    [Fact]
    public async Task Preregistered_WinsOverCimd_EvenWhenTheServerAdvertisesBoth()
    {
        // The spec's priority order: a client that already has credentials for this server uses
        // them. Getting this backwards is anthropics/claude-code#67258 - a row with a perfectly
        // good client id that still fell through and failed.
        var metadata = (await Discovery().DiscoverAsync(McpPlugin())).Value!.AuthorizationServer;

        var plugin = McpPlugin();
        plugin.OAuthClientSource = PluginConstants.OAuthClientSource.Preregistered;
        plugin.OAuthClientId = "operator-registered-client";

        var preregistered = new PreregisteredClientRegistrar();

        Assert.True(preregistered.CanResolve(plugin, metadata));

        var identity = await preregistered.ResolveAsync(plugin, metadata);

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal("operator-registered-client", identity.ClientId);
        Assert.Equal(PluginConstants.OAuthClientSource.Preregistered, identity.Source);
    }
}
