using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <inheritdoc />
public class McpClientMetadataProvider : IMcpClientMetadataProvider
{
    private readonly McpClientOptions _options;
    private readonly IMcpClientSigningKeyStore _signingKeys;

    public McpClientMetadataProvider(
        IOptions<McpClientOptions> options,
        IMcpClientSigningKeyStore signingKeys)
    {
        _options = options.Value;
        _signingKeys = signingKeys;
    }

    public McpClientMetadataDocumentDto? BuildClientMetadataDocument()
    {
        if (!ClientIdentifierUrl.IsValid(_options.ClientMetadataUrl)) return null;
        if (string.IsNullOrWhiteSpace(_options.RedirectUri)) return null;

        // Advertise private_key_jwt only when a key can actually back it. Negotiation is a server
        // picking the strongest method we list that it also accepts, so listing an unbacked method
        // is not a harmless hint - it is a flow that fails at the token request with nothing in the
        // exchange explaining why.
        var canSign = _signingKeys.HasSigningKey && !string.IsNullOrWhiteSpace(_options.JwksUrl);

        var supported = canSign
            ? new[]
            {
                PluginConstants.TokenEndpointAuthMethod.PrivateKeyJwt,
                PluginConstants.TokenEndpointAuthMethod.None,
            }
            : [PluginConstants.TokenEndpointAuthMethod.None];

        return new McpClientMetadataDocumentDto
        {
            // Byte-identical to the URL this is served from; servers compare the two and reject a
            // mismatch outright.
            ClientId = _options.ClientMetadataUrl,
            ClientName = _options.ClientName,
            ClientUri = NullIfBlank(_options.ClientUri),
            LogoUri = NullIfBlank(_options.LogoUri),
            PolicyUri = NullIfBlank(_options.PolicyUri),
            TosUri = NullIfBlank(_options.TosUri),
            Contacts = _options.Contacts.Count == 0 ? null : _options.Contacts,

            // Exactly one entry, permanently: every kind='mcp' plugin shares this callback, so a
            // new catalog row never changes this document. Editing it would mean waiting out the
            // caches every server keeps.
            RedirectUris = [_options.RedirectUri],

            GrantTypes = ["authorization_code", "refresh_token"],
            ResponseTypes = ["code"],

            // Strongest first, and it must appear inside the supported list - conformant servers
            // reject a document whose preferred method is not among the ones it advertises.
            TokenEndpointAuthMethod = supported[0],
            TokenEndpointAuthMethodsSupported = supported,
            TokenEndpointAuthSigningAlg = canSign ? _signingKeys.ActiveKey!.Algorithm : null,
            JwksUri = canSign ? _options.JwksUrl : null,
        };
    }

    public McpJsonWebKeySetDto BuildJwks() => new() { Keys = _signingKeys.PublishedKeys };

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
