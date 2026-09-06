using System.Net.Http.Headers;
using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <inheritdoc />
public class McpAuthorizationServerDiscovery : IMcpAuthorizationServerDiscovery
{
    private readonly HttpClient _httpClient;

    public McpAuthorizationServerDiscovery(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Result<McpServerDiscoveryDto>> DiscoverAsync(Plugin plugin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plugin.McpServerUrl)
            || !Uri.TryCreate(plugin.McpServerUrl, UriKind.Absolute, out var serverUri))
        {
            return Result.Failure<McpServerDiscoveryDto>(
                "Plugin has no usable MCP server URL.",
                PluginConstants.ErrorCodes.UnknownPlugin);
        }

        var resourceIdentifier = CanonicalResourceUri(serverUri);

        var resourceMetadata = await FetchProtectedResourceMetadataAsync(serverUri, ct);

        string issuer;
        Uri issuerUri;
        if (resourceMetadata is null)
        {
            // No RFC 9728 document anywhere. MCP Authorization 2025-06-18 made protected resource
            // metadata mandatory, but servers built against 2025-03-26 (Atlassian's hosted MCP
            // server, as of 2026-09) still publish only RFC 8414 metadata at their own origin and
            // expect the client to treat the server itself as the authorization server. The spec
            // keeps that as the documented backwards-compatibility path, so fall back to the
            // origin rather than refuse; a server that publishes nothing at all still fails on
            // the authorization-server lookup below, with the same error code as before.
            issuerUri = new Uri(serverUri.GetLeftPart(UriPartial.Authority));
            issuer = issuerUri.ToString().TrimEnd('/');
        }
        else
        {
            var advertised = ReadStringArray(resourceMetadata.Value, "authorization_servers").FirstOrDefault();
            if (string.IsNullOrWhiteSpace(advertised) || !Uri.TryCreate(advertised, UriKind.Absolute, out var parsedIssuer))
            {
                return Result.Failure<McpServerDiscoveryDto>(
                    $"Protected resource metadata for {resourceIdentifier} names no authorization server.",
                    PluginConstants.ErrorCodes.ProviderUnavailable);
            }

            issuer = advertised;
            issuerUri = parsedIssuer;
        }

        var asDocument = await FetchAuthorizationServerMetadataAsync(issuerUri, ct);
        if (asDocument is null)
        {
            return Result.Failure<McpServerDiscoveryDto>(
                $"Could not read authorization server metadata for {issuer}.",
                PluginConstants.ErrorCodes.ProviderUnavailable);
        }

        var document = asDocument.Value;
        var authorizationEndpoint = ReadString(document, "authorization_endpoint");
        var tokenEndpoint = ReadString(document, "token_endpoint");
        if (string.IsNullOrWhiteSpace(authorizationEndpoint) || string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            return Result.Failure<McpServerDiscoveryDto>(
                $"Authorization server {issuer} is missing an authorization or token endpoint.",
                PluginConstants.ErrorCodes.ProviderUnavailable);
        }

        // MCP Authorization makes verifying PKCE support a client MUST: "If
        // code_challenge_methods_supported is absent, the authorization server does not support
        // PKCE and MCP clients MUST refuse to proceed." Refusing here, loudly, is much cheaper to
        // diagnose than the unrelated-looking token failure it otherwise becomes.
        var codeChallengeMethods = ReadStringArray(document, "code_challenge_methods_supported");
        if (codeChallengeMethods.Count == 0)
        {
            return Result.Failure<McpServerDiscoveryDto>(
                $"Authorization server {issuer} does not advertise PKCE support "
                    + "(code_challenge_methods_supported is absent), so authorization cannot proceed safely.",
                PluginConstants.ErrorCodes.ProviderUnavailable);
        }

        var metadata = new AuthorizationServerMetadataDto(
            Issuer: ReadString(document, "issuer") ?? issuer,
            AuthorizationEndpoint: authorizationEndpoint,
            TokenEndpoint: tokenEndpoint,
            RevocationEndpoint: ReadString(document, "revocation_endpoint"),
            RegistrationEndpoint: ReadString(document, "registration_endpoint"),
            ClientIdMetadataDocumentSupported: ReadBool(document, "client_id_metadata_document_supported") ?? false,
            IssParameterSupported: ReadBool(document, "authorization_response_iss_parameter_supported") ?? false,
            CodeChallengeMethodsSupported: codeChallengeMethods,
            TokenEndpointAuthMethodsSupported: ReadStringArray(document, "token_endpoint_auth_methods_supported"),
            ScopesSupported: ReadStringArray(document, "scopes_supported"));

        // Without a protected resource document there is nothing to read resource scopes from;
        // the authorization server's own scopes_supported (in metadata) still applies.
        var resourceScopes = resourceMetadata is null
            ? Array.Empty<string>()
            : ReadStringArray(resourceMetadata.Value, "scopes_supported");

        return Result.Success(new McpServerDiscoveryDto(
            resourceIdentifier,
            resourceScopes,
            metadata));
    }

    /// <summary>
    /// RFC 8707 §2 canonical form: no fragment, and no trailing slash unless the path is
    /// semantically a directory. The same string must be sent as <c>resource</c> on both the
    /// authorization and token requests, so it is computed once and carried.
    /// </summary>
    private static string CanonicalResourceUri(Uri serverUri)
    {
        var builder = new UriBuilder(serverUri) { Fragment = string.Empty, Query = string.Empty };
        var canonical = builder.Uri.GetLeftPart(UriPartial.Path);
        return canonical.Length > 1 && canonical.EndsWith('/')
            ? canonical.TrimEnd('/')
            : canonical;
    }

    /// <summary>
    /// RFC 9728 discovery. The spec requires supporting both routes and preferring the header:
    /// an unauthenticated request answers 401 with <c>resource_metadata</c> in
    /// <c>WWW-Authenticate</c>; otherwise the well-known URIs are probed, path-scoped first.
    /// </summary>
    private async Task<JsonElement?> FetchProtectedResourceMetadataAsync(Uri serverUri, CancellationToken ct)
    {
        var advertised = await ProbeResourceMetadataUrlAsync(serverUri, ct);
        if (advertised is not null)
        {
            var fromHeader = await FetchJsonAsync(advertised, ct);
            if (fromHeader is not null) return fromHeader;
        }

        foreach (var candidate in WellKnownResourceMetadataUrls(serverUri))
        {
            var document = await FetchJsonAsync(candidate, ct);
            if (document is not null) return document;
        }

        return null;
    }

    private async Task<Uri?> ProbeResourceMetadataUrlAsync(Uri serverUri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, serverUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(request, ct);

            foreach (var header in response.Headers.WwwAuthenticate)
            {
                var resourceMetadata = ReadAuthParameter(header.Parameter, "resource_metadata");
                if (resourceMetadata is not null
                    && Uri.TryCreate(resourceMetadata, UriKind.Absolute, out var parsed))
                {
                    return parsed;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // The probe is an optimisation; the well-known fallback still gets its chance.
        }

        return null;
    }

    /// <summary>
    /// Reads one quoted parameter out of a <c>WWW-Authenticate</c> challenge without pulling in a
    /// full auth-params parser: the values we need are always quoted strings.
    /// </summary>
    private static string? ReadAuthParameter(string? challenge, string name)
    {
        if (string.IsNullOrWhiteSpace(challenge)) return null;

        var marker = $"{name}=\"";
        var start = challenge.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += marker.Length;
        var end = challenge.IndexOf('"', start);
        return end < 0 ? null : challenge[start..end];
    }

    /// <summary>
    /// Path-scoped first, then root, per RFC 9728: a server at <c>https://host/public/mcp</c> may
    /// publish at <c>https://host/.well-known/oauth-protected-resource/public/mcp</c>.
    /// </summary>
    private static IEnumerable<Uri> WellKnownResourceMetadataUrls(Uri serverUri)
    {
        var origin = serverUri.GetLeftPart(UriPartial.Authority);
        var path = serverUri.AbsolutePath.TrimEnd('/');

        if (!string.IsNullOrEmpty(path) && path != "/")
            yield return new Uri($"{origin}/.well-known/oauth-protected-resource{path}");

        yield return new Uri($"{origin}/.well-known/oauth-protected-resource");
    }

    /// <summary>
    /// RFC 8414 §3.1 with the §5 OpenID Connect compatibility note, in the priority order the MCP
    /// spec spells out. An issuer carrying a path gets the path-insertion forms first, because an
    /// issuer like <c>https://auth.example.com/tenant1</c> answers at a different place than a
    /// bare origin does.
    /// </summary>
    private async Task<JsonElement?> FetchAuthorizationServerMetadataAsync(Uri issuerUri, CancellationToken ct)
    {
        foreach (var candidate in WellKnownAuthorizationServerUrls(issuerUri))
        {
            var document = await FetchJsonAsync(candidate, ct);
            if (document is not null) return document;
        }

        return null;
    }

    private static IEnumerable<Uri> WellKnownAuthorizationServerUrls(Uri issuerUri)
    {
        var origin = issuerUri.GetLeftPart(UriPartial.Authority);
        var path = issuerUri.AbsolutePath.TrimEnd('/');
        var hasPath = !string.IsNullOrEmpty(path) && path != "/";

        if (hasPath)
        {
            yield return new Uri($"{origin}/.well-known/oauth-authorization-server{path}");
            yield return new Uri($"{origin}/.well-known/openid-configuration{path}");
            yield return new Uri($"{origin}{path}/.well-known/openid-configuration");
        }
        else
        {
            yield return new Uri($"{origin}/.well-known/oauth-authorization-server");
            yield return new Uri($"{origin}/.well-known/openid-configuration");
        }
    }

    private async Task<JsonElement?> FetchJsonAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var parsed = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return parsed.RootElement.Clone();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement document, string property) =>
        document.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement document, string property) =>
        document.TryGetProperty(property, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement document, string property)
    {
        if (!document.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }
}
