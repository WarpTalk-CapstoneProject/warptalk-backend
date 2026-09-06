using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Infrastructure.Mcp;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// Covers the client-registration ladder from WT-602: MCP Authorization 2026-07-28 fixes a
/// priority order (pre-registered, then Client ID Metadata Documents, then Dynamic Client
/// Registration) rather than offering a choice, and a client is expected to walk it, falling
/// through as ordinary control flow rather than throwing.
/// </summary>
public class McpClientRegistrarTests
{
    private static readonly Guid PluginId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static Plugin McpPlugin(
        string? clientId = null,
        string? clientSecretEncrypted = null,
        string oauthClientSource = "unresolved") =>
        new()
        {
            Id = PluginId,
            PluginKey = "linear",
            Label = "Linear",
            Description = "Issues and projects.",
            Provider = "linear",
            Kind = PluginConstants.PluginKind.Mcp,
            McpServerUrl = "https://mcp.linear.app/mcp",
            RequiredScopesJson = "[]",
            ToolsJson = "[]",
            OAuthClientId = clientId,
            OAuthClientSecretEncrypted = clientSecretEncrypted,
            OAuthClientSource = oauthClientSource,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static AuthorizationServerMetadataDto Metadata(
        bool cimdSupported = false,
        string? registrationEndpoint = null,
        IReadOnlyList<string>? tokenEndpointAuthMethods = null) =>
        new(
            Issuer: "https://auth.example.test",
            AuthorizationEndpoint: "https://auth.example.test/authorize",
            TokenEndpoint: "https://auth.example.test/token",
            RevocationEndpoint: null,
            RegistrationEndpoint: registrationEndpoint,
            ClientIdMetadataDocumentSupported: cimdSupported,
            IssParameterSupported: false,
            CodeChallengeMethodsSupported: ["S256"],
            TokenEndpointAuthMethodsSupported: tokenEndpointAuthMethods ?? [],
            ScopesSupported: []);

    // ---- PreregisteredClientRegistrar -------------------------------------------------------

    [Fact]
    public void PreregisteredClientRegistrar_CanResolve_WhenClientIdIsPresent_EvenIfCimdIsAdvertised()
    {
        // This is the anthropics/claude-code#67258 regression: a row that already has a usable
        // client id must not be routed past it toward a mechanism it does not need, no matter what
        // else the server advertises.
        var plugin = McpPlugin(clientId: "operator-supplied-id");
        var sut = new PreregisteredClientRegistrar();

        Assert.True(sut.CanResolve(plugin, Metadata(cimdSupported: true)));
    }

    [Fact]
    public async Task PreregisteredClientRegistrar_ResolvesWithNoSecret_AsPublicClient()
    {
        var plugin = McpPlugin(clientId: "operator-supplied-id");
        var sut = new PreregisteredClientRegistrar();

        var identity = await sut.ResolveAsync(plugin, Metadata());

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal("operator-supplied-id", identity.ClientId);
        Assert.Equal(PluginConstants.OAuthClientSource.Preregistered, identity.Source);
        Assert.Equal(PluginConstants.TokenEndpointAuthMethod.None, identity.TokenEndpointAuthMethod);
        Assert.Null(identity.EncryptedClientSecret);
    }

    [Fact]
    public async Task PreregisteredClientRegistrar_PrefersClientSecretPost_WhenServerAcceptsIt()
    {
        var plugin = McpPlugin(clientId: "id", clientSecretEncrypted: "protected:secret");
        var sut = new PreregisteredClientRegistrar();

        var identity = await sut.ResolveAsync(
            plugin,
            Metadata(tokenEndpointAuthMethods: ["client_secret_basic", "client_secret_post"]));

        Assert.Equal("client_secret_post", identity.TokenEndpointAuthMethod);
        Assert.Equal("protected:secret", identity.EncryptedClientSecret);
    }

    [Fact]
    public async Task PreregisteredClientRegistrar_PreservesDcrSource_ForARowThatAlreadyRegistered()
    {
        // A row whose credentials came from an earlier dynamic-registration call still has a
        // usable client id. Re-registering on every connect is wasteful and risks the server
        // pruning the old registration out from under a live connection, so this rung must not
        // relabel the row as freshly 'preregistered'.
        var plugin = McpPlugin(clientId: "dcr-issued-id", oauthClientSource: PluginConstants.OAuthClientSource.Dcr);
        var sut = new PreregisteredClientRegistrar();

        var identity = await sut.ResolveAsync(plugin, Metadata());

        Assert.Equal(PluginConstants.OAuthClientSource.Dcr, identity.Source);
    }

    [Fact]
    public async Task PreregisteredClientRegistrar_ReturnsUnsupported_WhenNoClientIdIsConfigured()
    {
        var plugin = McpPlugin();
        var sut = new PreregisteredClientRegistrar();

        var identity = await sut.ResolveAsync(plugin, Metadata());

        Assert.Equal(McpClientRegistrationOutcome.Unsupported, identity.Outcome);
        Assert.NotNull(identity.Detail);
    }

    // ---- CimdClientRegistrar -----------------------------------------------------------------

    private static CimdClientRegistrar CimdSut(string clientMetadataUrl = "https://warptalk.test/oauth/client-metadata/v1.json") =>
        new(Options.Create(new McpClientOptions { ClientMetadataUrl = clientMetadataUrl }));

    [Fact]
    public void CimdClientRegistrar_CannotResolve_WhenServerDoesNotAdvertiseSupport()
    {
        var sut = CimdSut();
        Assert.False(sut.CanResolve(McpPlugin(), Metadata(cimdSupported: false)));
    }

    [Theory]
    [InlineData("http://warptalk.test/oauth/client-metadata/v1.json")] // not https
    [InlineData("https://warptalk.test")]                              // no path component
    [InlineData("https://warptalk.test/")]                             // path is just "/"
    [InlineData("https://warptalk.test/oauth/../secret.json")]         // dot segment
    [InlineData("")]                                                    // unconfigured
    public void CimdClientRegistrar_CannotResolve_WhenConfiguredUrlIsUnusable(string url)
    {
        var sut = CimdSut(url);
        Assert.False(sut.CanResolve(McpPlugin(), Metadata(cimdSupported: true)));
    }

    [Fact]
    public async Task CimdClientRegistrar_ResolvesWithClientMetadataUrlAsClientId_AndNoSecret()
    {
        var sut = CimdSut();

        var identity = await sut.ResolveAsync(McpPlugin(), Metadata(cimdSupported: true));

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal("https://warptalk.test/oauth/client-metadata/v1.json", identity.ClientId);
        Assert.Equal(PluginConstants.OAuthClientSource.Cimd, identity.Source);
        Assert.Null(identity.EncryptedClientSecret);
    }

    [Fact]
    public async Task CimdClientRegistrar_PrefersPrivateKeyJwt_WhenServerAcceptsIt()
    {
        var sut = CimdSut();

        var identity = await sut.ResolveAsync(
            McpPlugin(),
            Metadata(cimdSupported: true, tokenEndpointAuthMethods: ["client_secret_basic", "private_key_jwt", "none"]));

        Assert.Equal("private_key_jwt", identity.TokenEndpointAuthMethod);
    }

    [Fact]
    public async Task CimdClientRegistrar_FallsBackToNone_WhenServerOnlyAcceptsSharedSecretMethods()
    {
        // The default capabilities of cloudflare/workers-oauth-provider once symmetric methods are
        // filtered out for a CIMD client: only 'none' survives. Declaring only private_key_jwt
        // would hard-fail against the largest population of CIMD-capable servers today.
        var sut = CimdSut();

        var identity = await sut.ResolveAsync(
            McpPlugin(),
            Metadata(cimdSupported: true, tokenEndpointAuthMethods: ["client_secret_basic", "client_secret_post", "none"]));

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal(PluginConstants.TokenEndpointAuthMethod.None, identity.TokenEndpointAuthMethod);
    }

    [Fact]
    public async Task CimdClientRegistrar_ResolvesWithNone_WhenServerAdvertisesNoAuthMethods()
    {
        var sut = CimdSut();

        var identity = await sut.ResolveAsync(McpPlugin(), Metadata(cimdSupported: true));

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal(PluginConstants.TokenEndpointAuthMethod.None, identity.TokenEndpointAuthMethod);
    }

    [Fact]
    public async Task CimdClientRegistrar_ReturnsUnsupported_WhenOnlySymmetricMethodsAreAcceptedAndNoneIsRefused()
    {
        // A pathological server that lists a shared-secret method but not 'none' leaves nothing a
        // CIMD client can use. This must surface as Unsupported, not as a thrown exception.
        var sut = CimdSut();

        var identity = await sut.ResolveAsync(
            McpPlugin(),
            Metadata(cimdSupported: true, tokenEndpointAuthMethods: ["client_secret_basic"]));

        Assert.Equal(McpClientRegistrationOutcome.Unsupported, identity.Outcome);
    }

    // ---- DynamicClientRegistrar ----------------------------------------------------------------

    [Fact]
    public void DynamicClientRegistrar_CannotResolve_WhenServerHasNoRegistrationEndpoint()
    {
        var sut = new DynamicClientRegistrar(
            new HttpClient(),
            Options.Create(new McpClientOptions { RedirectUri = "https://warptalk.test/callback" }),
            Substitute.For<IPluginCredentialProtector>(),
            NullLogger<DynamicClientRegistrar>.Instance);

        Assert.False(sut.CanResolve(McpPlugin(), Metadata(registrationEndpoint: null)));
    }

    [Fact]
    public void DynamicClientRegistrar_CanResolve_WhenServerAdvertisesRegistrationEndpoint()
    {
        var sut = new DynamicClientRegistrar(
            new HttpClient(),
            Options.Create(new McpClientOptions { RedirectUri = "https://warptalk.test/callback" }),
            Substitute.For<IPluginCredentialProtector>(),
            NullLogger<DynamicClientRegistrar>.Instance);

        Assert.True(sut.CanResolve(McpPlugin(), Metadata(registrationEndpoint: "https://auth.example.test/register")));
    }

    [Fact]
    public async Task DynamicClientRegistrar_ReturnsUnsupported_WhenNoRedirectUriIsConfigured()
    {
        var sut = new DynamicClientRegistrar(
            new HttpClient(),
            Options.Create(new McpClientOptions()),
            Substitute.For<IPluginCredentialProtector>(),
            NullLogger<DynamicClientRegistrar>.Instance);

        var identity = await sut.ResolveAsync(
            McpPlugin(),
            Metadata(registrationEndpoint: "https://auth.example.test/register"));

        Assert.Equal(McpClientRegistrationOutcome.Unsupported, identity.Outcome);
    }

    // ---- McpClientRegistrationResolver: the ladder itself -------------------------------------

    private static readonly Guid ResolverPluginId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task Resolver_PicksPreregistered_OverAnAdvertisedCimdFlag()
    {
        // Priority order per the spec: pre-registered beats CIMD even when the server would
        // support the latter.
        var plugin = McpPlugin(clientId: "operator-id");
        var resolver = BuildResolver();

        var identity = await resolver.ResolveAsync(plugin, Metadata(cimdSupported: true));

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal(PluginConstants.OAuthClientSource.Preregistered, identity.Source);
    }

    [Fact]
    public async Task Resolver_PicksCimd_WhenServerAdvertisesSupport_AndNoPreregisteredClientExists()
    {
        var plugin = McpPlugin();
        var resolver = BuildResolver();

        var identity = await resolver.ResolveAsync(plugin, Metadata(cimdSupported: true));

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal(PluginConstants.OAuthClientSource.Cimd, identity.Source);
    }

    [Fact]
    public async Task Resolver_FallsThroughToDcr_WhenNeitherPreregisteredNorCimdApplies()
    {
        var plugin = McpPlugin();
        var resolver = BuildResolver(dcrHttpHandler: FakeDcrHandler.ReturningClient("dcr-client-id"));

        var identity = await resolver.ResolveAsync(
            plugin,
            Metadata(cimdSupported: false, registrationEndpoint: "https://auth.example.test/register"));

        Assert.Equal(McpClientRegistrationOutcome.Resolved, identity.Outcome);
        Assert.Equal(PluginConstants.OAuthClientSource.Dcr, identity.Source);
        Assert.Equal("dcr-client-id", identity.ClientId);
    }

    [Fact]
    public async Task Resolver_ReturnsUnsupported_AsAValue_WhenAllThreeRungsAreExhausted()
    {
        // This is the reference-client failure mode from anthropics/claude-code#26675 /
        // #67258 / #76075: no pre-registered client, no CIMD, no registration endpoint. The
        // ladder must return a value here, never throw.
        var plugin = McpPlugin();
        var resolver = BuildResolver();

        var identity = await resolver.ResolveAsync(plugin, Metadata());

        Assert.Equal(McpClientRegistrationOutcome.Unsupported, identity.Outcome);
        Assert.NotNull(identity.Detail);
    }

    [Fact]
    public async Task Resolver_ReturnsProviderUnavailable_WhenDcrEndpointFailsOutright()
    {
        // A server that advertises a registration endpoint but rejects the request has told us
        // something about its availability, not about its capabilities - this must not be
        // reported as Unsupported, which would strand the plugin on a permanent-looking error.
        var plugin = McpPlugin();
        var resolver = BuildResolver(dcrHttpHandler: FakeDcrHandler.ReturningServerError());

        var identity = await resolver.ResolveAsync(
            plugin,
            Metadata(registrationEndpoint: "https://auth.example.test/register"));

        Assert.Equal(McpClientRegistrationOutcome.ProviderUnavailable, identity.Outcome);
    }

    private static McpClientRegistrationResolver BuildResolver(HttpMessageHandler? dcrHttpHandler = null)
    {
        var options = Options.Create(new McpClientOptions
        {
            ClientMetadataUrl = "https://warptalk.test/oauth/client-metadata/v1.json",
            RedirectUri = "https://warptalk.test/api/v1/assistant/plugins/mcp/oauth/callback",
        });

        var dcr = new DynamicClientRegistrar(
            new HttpClient(dcrHttpHandler ?? FakeDcrHandler.ReturningServerError()),
            options,
            Substitute.For<IPluginCredentialProtector>(),
            NullLogger<DynamicClientRegistrar>.Instance);

        return new McpClientRegistrationResolver(
        [
            new PreregisteredClientRegistrar(),
            new CimdClientRegistrar(options),
            dcr,
        ]);
    }

    /// <summary>Minimal fake transport so DCR registrars can be exercised without a real server.</summary>
    private sealed class FakeDcrHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        private FakeDcrHandler(HttpResponseMessage response) => _response = response;

        public static FakeDcrHandler ReturningClient(string clientId) => new(new HttpResponseMessage(System.Net.HttpStatusCode.Created)
        {
            Content = new StringContent($$"""{"client_id":"{{clientId}}"}""", System.Text.Encoding.UTF8, "application/json"),
        });

        public static FakeDcrHandler ReturningServerError() => new(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("unavailable"),
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_response);
    }
}
