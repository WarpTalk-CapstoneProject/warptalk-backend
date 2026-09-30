using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Infrastructure.Mcp;
using WarpTalk.AssistantService.Infrastructure.OAuth;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// What a token response that says nothing about scope means. WT-710.
/// </summary>
/// <remarks>
/// RFC 6749 §5.1 lets a server omit <c>scope</c> when it granted exactly what was requested.
/// Reading that silence as "no scopes" marked such connections partial and refused every tool with
/// <c>missing_scope</c>.
/// </remarks>
public class McpOAuthClientTokenScopeTests
{
    [Fact]
    public async Task ExchangeCodeAsync_ReadsAnOmittedScope_AsTheScopesThatWereRequested()
    {
        var token = await Exchange("""{"access_token":"a","token_type":"Bearer"}""", requested: ["mcp:read", "mcp:write"]);

        Assert.Equal(["mcp:read", "mcp:write"], token.GrantedScopes);
    }

    [Fact]
    public async Task ExchangeCodeAsync_StillReadsAnExplicitScope_AsWhatItSays()
    {
        var narrowed = await Exchange("""{"access_token":"a","scope":"mcp:read"}""", requested: ["mcp:read", "mcp:write"]);
        var empty = await Exchange("""{"access_token":"a","scope":""}""", requested: ["mcp:read"]);

        Assert.Equal(["mcp:read"], narrowed.GrantedScopes);
        Assert.Empty(empty.GrantedScopes);
    }

    private static Task<PluginOAuthTokenDto> Exchange(string tokenResponse, string[] requested)
    {
        var options = Options.Create(new McpClientOptions
        {
            RedirectUri = "https://warptalk.test/api/v1/assistant/plugins/mcp/oauth/callback",
        });

        var client = new McpOAuthClient(
            new HttpClient(new TokenEndpoint(tokenResponse)),
            options,
            new ConfigurationMcpClientSigningKeyStore(options, NullLogger<ConfigurationMcpClientSigningKeyStore>.Instance),
            Substitute.For<IPluginCredentialProtector>(),
            NullLogger<McpOAuthClient>.Instance);

        var plugin = new Plugin
        {
            Id = Guid.NewGuid(),
            PluginKey = "remote_app",
            Provider = "remote_app",
            Kind = PluginConstants.PluginKind.Mcp,
            McpServerUrl = "https://remote.test/mcp",
            OAuthAuthorizationEndpoint = "https://auth.remote.test/authorize",
            OAuthTokenEndpoint = "https://auth.remote.test/token",
            OAuthClientSource = PluginConstants.OAuthClientSource.Preregistered,
            OAuthClientId = "client-1",
            OAuthTokenEndpointAuthMethod = PluginConstants.TokenEndpointAuthMethod.None,
            RequiredScopesJson = "[]",
            ToolsJson = "[]",
        };

        return client.ExchangeCodeAsync(
            plugin,
            "code",
            new PluginOAuthStateDto(Guid.NewGuid(), "remote_app", CodeVerifier: "verifier", RequestedScopes: requested));
    }

    private sealed class TokenEndpoint(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
