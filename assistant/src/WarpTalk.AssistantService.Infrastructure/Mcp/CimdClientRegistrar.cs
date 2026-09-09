using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <summary>
/// Rung 2: our published metadata document URL is the <c>client_id</c>, so there is nothing to
/// register and nothing to store.
/// </summary>
/// <remarks>
/// The negotiation below mirrors what a conformant authorization server does on its side (see
/// <c>negotiateCimdTokenEndpointAuthMethod</c> in <c>cloudflare/workers-oauth-provider</c>), so
/// that a request we build is one the server can accept:
/// <list type="bullet">
///   <item>Shared-secret methods are unavailable to a CIMD client, full stop - the metadata
///   document is public, and a document carrying <c>client_secret</c> is rejected outright. So the
///   server's accepted set is filtered down to asymmetric methods plus <c>none</c>.</item>
///   <item>We advertise <c>private_key_jwt</c> first and <c>none</c> second. Advertising only
///   <c>private_key_jwt</c> would be a hard failure against the largest population of
///   CIMD-capable servers: a default-configured Workers OAuth provider accepts
///   <c>client_secret_basic</c>, <c>client_secret_post</c> and <c>none</c>, which after filtering
///   leaves <c>none</c> alone.</item>
/// </list>
/// Landing on <c>none</c> makes us a public client at that server. The compensating control is
/// topological rather than cryptographic: our redirect URI is a single HTTPS URL on a host we
/// control by DNS and TLS, so the code cannot be delivered anywhere else. That is a stronger
/// boundary than a shared secret against the attack this rung is exposed to, and it is why the
/// same trade-off would not be acceptable for a CLI binding a loopback port.
/// </remarks>
public class CimdClientRegistrar : IMcpClientRegistrar
{
    /// <summary>Strongest first. Only these two survive the CIMD filter on the server side.</summary>
    private static readonly string[] AdvertisedAuthMethods =
    [
        PluginConstants.TokenEndpointAuthMethod.PrivateKeyJwt,
        PluginConstants.TokenEndpointAuthMethod.None,
    ];

    private static readonly string[] SharedSecretAuthMethods =
    [
        "client_secret_basic",
        "client_secret_post",
        "client_secret_jwt",
    ];

    private readonly McpClientOptions _options;

    public CimdClientRegistrar(IOptions<McpClientOptions> options)
    {
        _options = options.Value;
    }

    public string Source => PluginConstants.OAuthClientSource.Cimd;

    public bool CanResolve(Plugin plugin, AuthorizationServerMetadataDto metadata) =>
        metadata.ClientIdMetadataDocumentSupported
        && ClientIdentifierUrl.IsValid(_options.ClientMetadataUrl);

    public Task<McpClientIdentityDto> ResolveAsync(
        Plugin plugin,
        AuthorizationServerMetadataDto metadata,
        CancellationToken ct = default)
    {
        if (!metadata.ClientIdMetadataDocumentSupported)
        {
            return Task.FromResult(McpClientIdentityDto.Unsupported(
                "The authorization server does not advertise Client ID Metadata Document support."));
        }

        var urlProblem = ClientIdentifierUrl.Validate(_options.ClientMetadataUrl);
        if (urlProblem is not null)
        {
            return Task.FromResult(McpClientIdentityDto.Unsupported(
                $"Plugins:Mcp:Client:ClientMetadataUrl is unusable as a client_id: {urlProblem}"));
        }

        var authMethod = NegotiateAuthMethod(metadata.TokenEndpointAuthMethodsSupported);
        if (authMethod is null)
        {
            return Task.FromResult(McpClientIdentityDto.Unsupported(
                "The authorization server accepts no token endpoint authentication method available to a "
                    + "metadata-document client; it advertises only "
                    + $"{string.Join(", ", metadata.TokenEndpointAuthMethodsSupported)}."));
        }

        // No secret, by construction: a public document cannot carry one.
        return Task.FromResult(McpClientIdentityDto.Resolved(
            _options.ClientMetadataUrl,
            PluginConstants.OAuthClientSource.Cimd,
            authMethod));
    }

    private static string? NegotiateAuthMethod(IReadOnlyList<string> serverMethods)
    {
        // A server that advertises nothing gets the CIMD default rather than OAuth's usual
        // client_secret_basic, which a metadata-document client could never satisfy anyway.
        if (serverMethods.Count == 0) return PluginConstants.TokenEndpointAuthMethod.None;

        var accepted = serverMethods
            .Where(method => !SharedSecretAuthMethods.Contains(method, StringComparer.Ordinal))
            .ToArray();

        return AdvertisedAuthMethods.FirstOrDefault(ours => accepted.Contains(ours, StringComparer.Ordinal));
    }
}
