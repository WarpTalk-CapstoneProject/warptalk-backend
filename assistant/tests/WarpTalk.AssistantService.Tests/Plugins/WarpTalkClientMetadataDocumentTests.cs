using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Infrastructure.Mcp;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The contract between WarpTalk and every authorization server that supports Client ID Metadata
/// Documents.
/// </summary>
/// <remarks>
/// Each rule here is enforced by shipped servers, so breaking one does not fail locally - it fails
/// as a rejected authorization against a real provider, which is a far worse place to find out.
/// That is why this runs in CI: it is the only thing standing between an innocuous-looking
/// configuration edit and every MCP plugin silently becoming unauthenticatable.
/// </remarks>
public class WarpTalkClientMetadataDocumentTests
{
    private const string MetadataUrl = "https://warptalk.test/oauth/client-metadata/v1.json";
    private const string JwksUrl = "https://warptalk.test/oauth/client-metadata/jwks.json";
    private const string RedirectUri = "https://warptalk.test/api/v1/assistant/plugins/mcp/oauth/callback";

    private static McpClientOptions FullyConfigured(params (string Kid, string Pem)[] keys) => new()
    {
        ClientName = "WarpTalk",
        ClientMetadataUrl = MetadataUrl,
        JwksUrl = JwksUrl,
        RedirectUri = RedirectUri,
        ClientUri = "https://warptalk.test",
        LogoUri = "https://warptalk.test/brand/warptalk-192.png",
        PolicyUri = "https://warptalk.test/legal/privacy",
        TosUri = "https://warptalk.test/legal/terms",
        Contacts = ["support@warptalk.test"],
        SigningKeys = keys
            .Select(k => new McpClientSigningKeyOptions { Kid = k.Kid, PrivateKeyPem = k.Pem })
            .ToList(),
    };

    private static (McpClientMetadataProvider Provider, ConfigurationMcpClientSigningKeyStore Keys) Build(
        McpClientOptions options)
    {
        var keys = new ConfigurationMcpClientSigningKeyStore(
            Options.Create(options),
            NullLogger<ConfigurationMcpClientSigningKeyStore>.Instance);

        return (new McpClientMetadataProvider(Options.Create(options), keys), keys);
    }

    private static (string Kid, string Pem) NewKey(string kid)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (kid, key.ExportPkcs8PrivateKeyPem());
    }

    private static JsonElement Serialize(object document) =>
        JsonDocument.Parse(JsonSerializer.Serialize(document)).RootElement.Clone();

    // ---- Client Identifier URL rules ---------------------------------------------------------

    [Fact]
    public void ClientId_IsByteIdenticalToTheUrlTheDocumentIsServedFrom()
    {
        // Conformant servers fetch the URL, read client_id out of the body, and reject the request
        // outright when the two differ. There is no normalisation step in that comparison.
        var (provider, keys) = Build(FullyConfigured());
        using var keyStore = keys;

        var document = provider.BuildClientMetadataDocument()!;

        Assert.Equal(MetadataUrl, document.ClientId);
    }

    [Fact]
    public void ClientIdentifierUrl_SatisfiesTheSpecRules()
    {
        Assert.Null(ClientIdentifierUrl.Validate(MetadataUrl));
    }

    [Theory]
    [InlineData("http://warptalk.test/oauth/client-metadata/v1.json")]
    [InlineData("https://warptalk.test")]
    [InlineData("https://warptalk.test/")]
    [InlineData("https://user:pw@warptalk.test/oauth/v1.json")]
    [InlineData("https://warptalk.test/oauth/v1.json#frag")]
    [InlineData("https://warptalk.test/oauth/../secret.json")]
    public void NoDocumentIsPublished_WhenTheConfiguredUrlBreaksAnyRule(string url)
    {
        // 404 rather than a malformed document: a document a provider fetches, parses and rejects
        // is a much more confusing failure than a URL that simply does not resolve.
        var options = FullyConfigured();
        options.ClientMetadataUrl = url;

        var (provider, keys) = Build(options);
        using var keyStore = keys;

        Assert.Null(provider.BuildClientMetadataDocument());
    }

    [Fact]
    public void NoDocumentIsPublished_WhenNoRedirectUriIsConfigured()
    {
        var options = FullyConfigured();
        options.RedirectUri = string.Empty;

        var (provider, keys) = Build(options);
        using var keyStore = keys;

        Assert.Null(provider.BuildClientMetadataDocument());
    }

    // ---- Required and forbidden members ------------------------------------------------------

    [Fact]
    public void RequiredMembersArePresentAndNonEmpty()
    {
        var (provider, keys) = Build(FullyConfigured());
        using var keyStore = keys;

        var json = Serialize(provider.BuildClientMetadataDocument()!);

        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("client_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("client_name").GetString()));
        Assert.NotEmpty(json.GetProperty("redirect_uris").EnumerateArray());
    }

    [Fact]
    public void DocumentNeverCarriesASecret()
    {
        // Structural, not incidental: the document is public, and conformant servers reject any
        // document containing client_secret or client_secret_expires_at outright.
        var (provider, keys) = Build(FullyConfigured(NewKey("k1")));
        using var keyStore = keys;

        var json = Serialize(provider.BuildClientMetadataDocument()!);

        Assert.False(json.TryGetProperty("client_secret", out _));
        Assert.False(json.TryGetProperty("client_secret_expires_at", out _));
    }

    [Fact]
    public void PublishedJwks_ContainsNoPrivateKeyMaterial()
    {
        var (provider, keys) = Build(FullyConfigured(NewKey("k1"), NewKey("k2")));
        using var keyStore = keys;

        var json = Serialize(provider.BuildJwks());
        var privateMembers = new[] { "d", "p", "q", "dp", "dq", "qi", "oth", "k" };

        foreach (var jwk in json.GetProperty("keys").EnumerateArray())
        {
            foreach (var member in privateMembers)
                Assert.False(jwk.TryGetProperty(member, out _), $"JWK leaked private member '{member}'.");
        }
    }

    [Fact]
    public void Document_StaysWellUnderTheFiveKilobyteCap()
    {
        var (provider, keys) = Build(FullyConfigured(NewKey("k1"), NewKey("k2")));
        using var keyStore = keys;

        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(provider.BuildClientMetadataDocument()!));

        Assert.True(bytes <= 5 * 1024, $"Client metadata document is {bytes} bytes; servers cap the fetch at 5 KB.");
    }

    // ---- Auth method negotiability -----------------------------------------------------------

    [Fact]
    public void PreferredAuthMethod_AppearsInsideTheSupportedList()
    {
        // A server validating the document rejects one whose preferred method is not among the
        // methods it advertises supporting.
        var (provider, keys) = Build(FullyConfigured(NewKey("k1")));
        using var keyStore = keys;

        var document = provider.BuildClientMetadataDocument()!;

        Assert.Contains(document.TokenEndpointAuthMethod, document.TokenEndpointAuthMethodsSupported);
    }

    [Fact]
    public void PrivateKeyJwtIsAdvertisedFirst_WhenAKeyCanBackIt()
    {
        var (provider, keys) = Build(FullyConfigured(NewKey("k1")));
        using var keyStore = keys;

        var document = provider.BuildClientMetadataDocument()!;

        Assert.Equal("private_key_jwt", document.TokenEndpointAuthMethod);
        Assert.Equal(["private_key_jwt", "none"], document.TokenEndpointAuthMethodsSupported);
        Assert.Equal("ES256", document.TokenEndpointAuthSigningAlg);
        Assert.Equal(JwksUrl, document.JwksUri);
    }

    [Fact]
    public void NoneIsTheOnlyAdvertisedMethod_WhenNoSigningKeyExists()
    {
        // Advertising a capability the process cannot back is worse than not advertising it: the
        // server picks the strongest method we list, and the flow then dies at the token request
        // with nothing in the exchange explaining why.
        var (provider, keys) = Build(FullyConfigured());
        using var keyStore = keys;

        var document = provider.BuildClientMetadataDocument()!;

        Assert.Equal("none", document.TokenEndpointAuthMethod);
        Assert.Equal(["none"], document.TokenEndpointAuthMethodsSupported);
        Assert.Null(document.TokenEndpointAuthSigningAlg);
        Assert.Null(document.JwksUri);
    }

    [Fact]
    public void JwksUriIsOmitted_WhenAKeyExistsButNoJwksUrlIsConfigured()
    {
        var options = FullyConfigured(NewKey("k1"));
        options.JwksUrl = string.Empty;

        var (provider, keys) = Build(options);
        using var keyStore = keys;

        var document = provider.BuildClientMetadataDocument()!;

        Assert.Equal("none", document.TokenEndpointAuthMethod);
        Assert.Null(document.JwksUri);
    }

    // ---- Redirect URIs and grant pairing -----------------------------------------------------

    [Fact]
    public void ExactlyOneRedirectUriIsPublished()
    {
        // Permanently one: every kind='mcp' plugin shares the same callback, which is what lets a
        // new catalog row be an insert rather than a re-publish of this document.
        var (provider, keys) = Build(FullyConfigured());
        using var keyStore = keys;

        var document = provider.BuildClientMetadataDocument()!;

        Assert.Equal([RedirectUri], document.RedirectUris);
    }

    [Fact]
    public void AuthorizationCodeAndCodeAreRegisteredTogether()
    {
        // Servers reject a document registering one without the other.
        var (provider, keys) = Build(FullyConfigured());
        using var keyStore = keys;

        var document = provider.BuildClientMetadataDocument()!;

        Assert.Contains("authorization_code", document.GrantTypes);
        Assert.Contains("code", document.ResponseTypes);
        Assert.Contains("refresh_token", document.GrantTypes);
    }

    [Fact]
    public void DisplayMetadataReachesTheConsentScreen()
    {
        // Not decoration: an operator deciding whether to trust an unknown domain sees exactly
        // these fields, and a document carrying only the three required members reads as anonymous.
        var (provider, keys) = Build(FullyConfigured());
        using var keyStore = keys;

        var json = Serialize(provider.BuildClientMetadataDocument()!);

        foreach (var member in new[] { "client_uri", "logo_uri", "policy_uri", "tos_uri", "contacts" })
            Assert.True(json.TryGetProperty(member, out _), $"Document omits '{member}'.");
    }

    [Fact]
    public void UnsetDisplayMembersAreOmitted_NotSerialisedAsNull()
    {
        // "logo_uri": null is not the same as omitting it, and several servers validate member
        // types strictly enough to fail on the former.
        var options = FullyConfigured();
        options.LogoUri = string.Empty;
        options.Contacts = [];

        var (provider, keys) = Build(options);
        using var keyStore = keys;

        var json = Serialize(provider.BuildClientMetadataDocument()!);

        Assert.False(json.TryGetProperty("logo_uri", out _));
        Assert.False(json.TryGetProperty("contacts", out _));
    }
}
