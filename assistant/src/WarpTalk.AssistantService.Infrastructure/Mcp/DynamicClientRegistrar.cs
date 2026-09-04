using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <summary>
/// Rung 3: RFC 7591 dynamic registration. Deprecated, and kept only for authorization servers that
/// have not adopted Client ID Metadata Documents.
/// </summary>
/// <remarks>
/// MCP Authorization 2026-07-28 marks this mechanism deprecated and retained for backwards
/// compatibility; removal is expected after summer 2027. Every successful resolution therefore
/// emits a structured log so that, before the deadline, the rows still depending on it can be
/// enumerated from telemetry rather than guessed at.
/// <para>
/// Registration failure is reported as <c>ProviderUnavailable</c>, never <c>Unsupported</c>: the
/// server told us it has a registration endpoint, so a failure here is an outage or a rejected
/// request, not evidence about what the server supports. Treating it as the latter would strand
/// the plugin on a permanent-looking error after a transient one.
/// </para>
/// </remarks>
public class DynamicClientRegistrar : IMcpClientRegistrar
{
    private readonly HttpClient _httpClient;
    private readonly McpClientOptions _options;
    private readonly IPluginCredentialProtector _credentialProtector;
    private readonly ILogger<DynamicClientRegistrar> _logger;

    public DynamicClientRegistrar(
        HttpClient httpClient,
        IOptions<McpClientOptions> options,
        IPluginCredentialProtector credentialProtector,
        ILogger<DynamicClientRegistrar> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _credentialProtector = credentialProtector;
        _logger = logger;
    }

    public string Source => PluginConstants.OAuthClientSource.Dcr;

    public bool CanResolve(Plugin plugin, AuthorizationServerMetadataDto metadata) =>
        !string.IsNullOrWhiteSpace(metadata.RegistrationEndpoint);

    public async Task<McpClientIdentityDto> ResolveAsync(
        Plugin plugin,
        AuthorizationServerMetadataDto metadata,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metadata.RegistrationEndpoint))
        {
            return McpClientIdentityDto.Unsupported(
                "The authorization server does not advertise a dynamic registration endpoint.");
        }

        if (string.IsNullOrWhiteSpace(_options.RedirectUri))
        {
            return McpClientIdentityDto.Unsupported(
                "No redirect URI is configured (Plugins:Mcp:Client:RedirectUri).");
        }

        var request = new Dictionary<string, object>
        {
            ["client_name"] = _options.ClientName,
            ["redirect_uris"] = new[] { _options.RedirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = PluginConstants.TokenEndpointAuthMethod.ClientSecretPost,
        };

        JsonElement document;
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(metadata.RegistrationEndpoint, request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return McpClientIdentityDto.Unavailable(
                    $"Dynamic client registration failed with HTTP {(int)response.StatusCode}: {Truncate(body)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var parsed = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            document = parsed.RootElement.Clone();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return McpClientIdentityDto.Unavailable($"Dynamic client registration could not complete: {e.Message}");
        }

        var clientId = ReadString(document, "client_id");
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return McpClientIdentityDto.Unavailable(
                "Dynamic client registration returned no client_id.");
        }

        var clientSecret = ReadString(document, "client_secret");
        var negotiated = ReadString(document, "token_endpoint_auth_method")
            ?? (string.IsNullOrWhiteSpace(clientSecret)
                ? PluginConstants.TokenEndpointAuthMethod.None
                : PluginConstants.TokenEndpointAuthMethod.ClientSecretPost);

        _logger.LogInformation(
            "mcp_client_registration_deprecated_path plugin_key={PluginKey} issuer={Issuer} "
                + "auth_method={AuthMethod}. RFC 7591 dynamic client registration is deprecated by "
                + "MCP Authorization and is expected to be removed after summer 2027; this plugin "
                + "will need a pre-registered client or CIMD support before then.",
            plugin.PluginKey,
            metadata.Issuer,
            negotiated);

        return McpClientIdentityDto.Resolved(
            clientId,
            PluginConstants.OAuthClientSource.Dcr,
            negotiated,
            string.IsNullOrWhiteSpace(clientSecret) ? null : _credentialProtector.Protect(clientSecret));
    }

    private static string? ReadString(JsonElement document, string property) =>
        document.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string body) =>
        body.Length <= 300 ? body : body[..300] + "...";
}
