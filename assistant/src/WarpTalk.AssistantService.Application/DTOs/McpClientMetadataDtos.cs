using System.Text.Json.Serialization;

namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>
/// WarpTalk's Client ID Metadata Document, serialised verbatim to the wire.
/// </summary>
/// <remarks>
/// The JSON names are the contract with every authorization server, so they are pinned explicitly
/// rather than left to a serializer policy. Null members are omitted: a document that carries
/// <c>"jwks_uri": null</c> is not the same as one that omits it, and several servers validate
/// member types strictly.
/// <para>
/// <see cref="ClientId"/> must equal the URL this document is served from, byte for byte -
/// conformant servers compare the two and reject a mismatch outright.
/// </para>
/// </remarks>
public record McpClientMetadataDocumentDto
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("client_name")]
    public required string ClientName { get; init; }

    [JsonPropertyName("client_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientUri { get; init; }

    [JsonPropertyName("logo_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogoUri { get; init; }

    [JsonPropertyName("policy_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PolicyUri { get; init; }

    [JsonPropertyName("tos_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TosUri { get; init; }

    [JsonPropertyName("contacts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Contacts { get; init; }

    [JsonPropertyName("redirect_uris")]
    public required IReadOnlyList<string> RedirectUris { get; init; }

    [JsonPropertyName("grant_types")]
    public required IReadOnlyList<string> GrantTypes { get; init; }

    [JsonPropertyName("response_types")]
    public required IReadOnlyList<string> ResponseTypes { get; init; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public required string TokenEndpointAuthMethod { get; init; }

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public required IReadOnlyList<string> TokenEndpointAuthMethodsSupported { get; init; }

    [JsonPropertyName("token_endpoint_auth_signing_alg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenEndpointAuthSigningAlg { get; init; }

    [JsonPropertyName("jwks_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JwksUri { get; init; }
}

/// <summary>The published JWKS. Public parameters only, by definition.</summary>
public record McpJsonWebKeySetDto
{
    [JsonPropertyName("keys")]
    public required IReadOnlyList<McpPublicJsonWebKeyDto> Keys { get; init; }
}
