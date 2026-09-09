namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>
/// The authorization server's own metadata, narrowed to the fields the registration ladder and the
/// authorization-code flow actually consume.
/// </summary>
/// <remarks>
/// Assembled by <c>IMcpAuthorizationServerDiscovery</c> from RFC 9728 protected-resource metadata
/// followed by RFC 8414 or OpenID Connect Discovery, so nothing downstream needs to know which of
/// the well-known documents answered.
/// <para>
/// <see cref="CodeChallengeMethodsSupported"/> being empty is not a detail to shrug at: MCP
/// Authorization makes PKCE support a <c>MUST</c> for clients to verify, and requires refusing to
/// proceed when the server does not advertise it.
/// </para>
/// </remarks>
public record AuthorizationServerMetadataDto(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RevocationEndpoint,
    string? RegistrationEndpoint,
    bool ClientIdMetadataDocumentSupported,
    bool IssParameterSupported,
    IReadOnlyList<string> CodeChallengeMethodsSupported,
    IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
    IReadOnlyList<string> ScopesSupported);

/// <summary>
/// Everything one round of discovery learned about an MCP server and the authorization server
/// behind it.
/// </summary>
/// <remarks>
/// <see cref="ResourceIdentifier"/> is the canonical MCP server URI, and is what goes into the
/// RFC 8707 <c>resource</c> parameter on both the authorization and token requests.
/// <para>
/// The two scope lists are deliberately kept apart. <see cref="ResourceScopesSupported"/> comes
/// from the protected resource's own metadata and is the spec's fallback for scope selection;
/// <see cref="AuthorizationServerMetadataDto.ScopesSupported"/> comes from the authorization
/// server and is a last resort. Collapsing them loses the case that matters: a resource that
/// advertises <c>"scopes_supported": []</c> while its identity provider advertises a perfectly
/// usable set. Sending no <c>scope</c> at all there produces an authorization code the token
/// endpoint then refuses - see anthropics/claude-code#90190.
/// </para>
/// </remarks>
public record McpServerDiscoveryDto(
    string ResourceIdentifier,
    IReadOnlyList<string> ResourceScopesSupported,
    AuthorizationServerMetadataDto AuthorizationServer);

/// <summary>
/// How the attempt to establish a client identity for one plugin ended.
/// </summary>
/// <remarks>
/// Modelled on <see cref="PluginOAuthRefreshOutcome"/> for the same reason: the caller has to act
/// differently on each of these, and none of them is exceptional enough to throw. Throwing is what
/// makes the reference MCP clients unusable - a server without dynamic registration surfaces as a
/// raw <c>Incompatible auth server</c> string at the status call, before the user is offered any
/// way forward.
/// </remarks>
public enum McpClientRegistrationOutcome
{
    /// <summary>A client identity is available and the flow can proceed.</summary>
    Resolved,

    /// <summary>
    /// Every rung was exhausted: no pre-registered client on the row, no Client ID Metadata
    /// Document support, no registration endpoint. An operator can fix this by registering an app
    /// with the provider and supplying the client id, so it must reach the user as guidance.
    /// </summary>
    Unsupported,

    /// <summary>
    /// Discovery or dynamic registration failed for a reason that says nothing about what the
    /// server supports - outage, timeout, an unparseable response. Retrying later is right;
    /// recording a rung is not.
    /// </summary>
    ProviderUnavailable,
}

/// <summary>
/// The client identity one plugin will present to its authorization server.
/// </summary>
/// <remarks>
/// <see cref="ClientId"/>, <see cref="Source"/> and <see cref="TokenEndpointAuthMethod"/> are
/// non-null exactly when <see cref="Outcome"/> is
/// <see cref="McpClientRegistrationOutcome.Resolved"/>.
/// <para>
/// <see cref="EncryptedClientSecret"/> stays protected in transit through Application; only the
/// Infrastructure OAuth client unprotects it, immediately before the token request. It is null for
/// a CIMD client by construction - a public metadata document cannot carry a shared secret, and
/// conformant servers reject any document that tries.
/// </para>
/// </remarks>
public record McpClientIdentityDto(
    McpClientRegistrationOutcome Outcome,
    string? ClientId,
    string? Source,
    string? TokenEndpointAuthMethod,
    string? EncryptedClientSecret = null,
    string? Detail = null)
{
    public static McpClientIdentityDto Resolved(
        string clientId,
        string source,
        string tokenEndpointAuthMethod,
        string? encryptedClientSecret = null) =>
        new(McpClientRegistrationOutcome.Resolved, clientId, source, tokenEndpointAuthMethod, encryptedClientSecret);

    public static McpClientIdentityDto Unsupported(string detail) =>
        new(McpClientRegistrationOutcome.Unsupported, null, null, null, null, detail);

    public static McpClientIdentityDto Unavailable(string detail) =>
        new(McpClientRegistrationOutcome.ProviderUnavailable, null, null, null, null, detail);
}

/// <summary>
/// What a provisioned <c>kind='mcp'</c> plugin needs to start an authorization request.
/// </summary>
/// <remarks>
/// <see cref="Applies"/> is false for a <c>native</c> plugin, which has its own compiled-in OAuth
/// client and never walks the ladder. Callers branch on it rather than on the plugin kind, so the
/// kind check lives in exactly one place.
/// </remarks>
public record McpClientContextDto(
    bool Applies,
    McpServerDiscoveryDto? Discovery,
    McpClientIdentityDto? Identity)
{
    public static readonly McpClientContextDto NotApplicable = new(false, null, null);
}

/// <summary>
/// A key WarpTalk signs client assertions with. The private material never leaves Infrastructure.
/// </summary>
public record McpSigningKeyDto(string Kid, string Algorithm);

/// <summary>
/// One entry of our published JWKS: public parameters only, by definition.
/// </summary>
/// <remarks>
/// Named after the JWK members rather than flattened into something friendlier, because this is
/// serialised straight to the wire and the field names are the contract.
/// </remarks>
public record McpPublicJsonWebKeyDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("kty")] string Kty,
    [property: System.Text.Json.Serialization.JsonPropertyName("crv")] string Crv,
    [property: System.Text.Json.Serialization.JsonPropertyName("x")] string X,
    [property: System.Text.Json.Serialization.JsonPropertyName("y")] string Y,
    [property: System.Text.Json.Serialization.JsonPropertyName("kid")] string Kid,
    [property: System.Text.Json.Serialization.JsonPropertyName("alg")] string Alg,
    [property: System.Text.Json.Serialization.JsonPropertyName("use")] string Use);
