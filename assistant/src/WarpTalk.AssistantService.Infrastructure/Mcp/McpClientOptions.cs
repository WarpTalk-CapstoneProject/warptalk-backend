namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <summary>
/// How WarpTalk identifies itself to the authorization servers behind remote MCP servers.
/// Bound from <c>Plugins:Mcp:Client</c>.
/// </summary>
public class McpClientOptions
{
    /// <summary>
    /// Shown on the consent screen, and sent as <c>client_name</c> during dynamic registration.
    /// </summary>
    public string ClientName { get; set; } = "WarpTalk";

    /// <summary>
    /// The single redirect URI every <c>kind='mcp'</c> plugin uses. It is deliberately not
    /// per-plugin: a Client ID Metadata Document has to enumerate its redirect URIs and the
    /// authorization server matches them exactly, so a per-plugin path would mean editing - and
    /// re-publishing - that document every time a catalog row is added. The plugin key travels in
    /// the protected <c>state</c> instead.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Absolute HTTPS URL of our published Client ID Metadata Document, which doubles as the
    /// <c>client_id</c> presented to servers that support CIMD. Must carry a path component and
    /// must equal the <c>client_id</c> inside the document byte for byte. Empty disables the CIMD
    /// rung, which is the correct configuration until the document is reachable on a public host.
    /// </summary>
    public string ClientMetadataUrl { get; set; } = string.Empty;

    /// <summary>
    /// Absolute HTTPS URL of our published JWKS. Only meaningful when <see cref="SigningKeys"/> is
    /// non-empty; the metadata document omits both <c>jwks_uri</c> and <c>private_key_jwt</c>
    /// otherwise, because advertising a capability the key set cannot back is worse than not
    /// advertising it.
    /// </summary>
    public string JwksUrl { get; set; } = string.Empty;

    /// <summary>Homepage shown beside our name on a provider's consent screen.</summary>
    public string ClientUri { get; set; } = string.Empty;

    /// <summary>Logo shown on the consent screen.</summary>
    public string LogoUri { get; set; } = string.Empty;

    /// <summary>Privacy policy linked from the consent screen.</summary>
    public string PolicyUri { get; set; } = string.Empty;

    /// <summary>Terms of service linked from the consent screen.</summary>
    public string TosUri { get; set; } = string.Empty;

    /// <summary>
    /// Developer contacts. Together with the URIs above this is not decoration: a server operator
    /// deciding whether to trust an unknown domain sees exactly these fields, and a document
    /// carrying only the three required members reads as anonymous.
    /// </summary>
    public List<string> Contacts { get; set; } = [];

    /// <summary>
    /// ES256 keys used to authenticate as a <c>private_key_jwt</c> client, active key first.
    /// More than one may be listed so a rotation overlaps: a server that cached our JWKS must
    /// still find the retiring <c>kid</c> while in-flight assertions drain.
    /// </summary>
    public List<McpClientSigningKeyOptions> SigningKeys { get; set; } = [];
}

public class McpClientSigningKeyOptions
{
    /// <summary>Published in the JWKS and echoed in each assertion header; must be stable.</summary>
    public string Kid { get; set; } = string.Empty;

    /// <summary>PKCS#8 or SEC1 PEM for a P-256 private key. Supplied as a deployment secret.</summary>
    public string PrivateKeyPem { get; set; } = string.Empty;
}
