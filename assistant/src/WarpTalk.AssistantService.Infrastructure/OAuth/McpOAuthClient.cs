using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Infrastructure.Mcp;

namespace WarpTalk.AssistantService.Infrastructure.OAuth;

/// <summary>
/// The OAuth 2.1 client every <c>kind='mcp'</c> plugin uses, whatever rung of the registration
/// ladder gave it a client id.
/// </summary>
/// <remarks>
/// Unlike <see cref="GoogleWorkspaceOAuthClient"/> this client reads its endpoints from the plugin
/// row, which authorization-server discovery filled in during provisioning. That is what lets a new
/// MCP app be an insert rather than a class.
/// <para>
/// Four requirements here are <c>MUST</c>s in MCP Authorization, and each one has a documented
/// failure in the wild when skipped:
/// </para>
/// <list type="bullet">
///   <item><b>PKCE S256</b> on both legs.</item>
///   <item><b>RFC 8707 <c>resource</c></b> on both the authorization and token requests, so the
///   token is audience-bound to this MCP server and cannot be replayed at another.</item>
///   <item><b>An explicit <c>scope</c></b>, chosen from the protected resource's metadata and
///   falling back to the authorization server's own advertised scopes. Omitting it is how
///   anthropics/claude-code#90190 produces an authorization code that the token endpoint then
///   refuses with <c>invalid_grant</c> - a failure with nothing in the exchange pointing at the
///   cause.</item>
///   <item><b>RFC 9207 <c>iss</c> validation</b> against the issuer recorded before the redirect,
///   which is what closes authorization-server mix-up.</item>
/// </list>
/// </remarks>
public class McpOAuthClient : IPluginOAuthClient
{
    /// <summary>Short enough that a captured assertion is worthless, long enough for a slow token endpoint.</summary>
    private static readonly TimeSpan ClientAssertionLifetime = TimeSpan.FromMinutes(2);

    private const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly HttpClient _httpClient;
    private readonly McpClientOptions _options;
    private readonly ConfigurationMcpClientSigningKeyStore _signingKeys;
    private readonly IPluginCredentialProtector _credentialProtector;
    private readonly ILogger<McpOAuthClient> _logger;

    public McpOAuthClient(
        HttpClient httpClient,
        IOptions<McpClientOptions> options,
        ConfigurationMcpClientSigningKeyStore signingKeys,
        IPluginCredentialProtector credentialProtector,
        ILogger<McpOAuthClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _signingKeys = signingKeys;
        _credentialProtector = credentialProtector;
        _logger = logger;
    }

    /// <summary>
    /// Mints the PKCE verifier and records the issuer discovery validated, both of which have to
    /// survive the browser round trip inside the sealed state.
    /// </summary>
    public PluginOAuthStateDto PrepareState(Plugin plugin, PluginOAuthStateDto state) =>
        state with
        {
            CodeVerifier = CreateCodeVerifier(),
            // Recorded now, from the metadata discovery already validated. Comparing the callback's
            // iss against anything re-fetched later would defeat the check entirely.
            Issuer = plugin.OAuthAuthorizationEndpoint is null ? null : IssuerOf(plugin),
        };

    public string BuildAuthorizationUrl(
        Plugin plugin,
        IReadOnlyList<string> scopes,
        string protectedState,
        PluginOAuthStateDto flowState)
    {
        var authorizationEndpoint = Require(plugin.OAuthAuthorizationEndpoint, plugin, "authorization endpoint");
        var clientId = Require(plugin.OAuthClientId, plugin, "client id");

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = clientId;
        query["redirect_uri"] = _options.RedirectUri;
        query["state"] = protectedState;
        query["code_challenge"] = CreateCodeChallenge(flowState.CodeVerifier!);
        query["code_challenge_method"] = "S256";

        // Sent regardless of whether the server advertises support: a server that ignores it is
        // unharmed, and one that honours it binds the token to this MCP server alone.
        query["resource"] = ResourceIdentifier(plugin);

        if (scopes.Count > 0) query["scope"] = string.Join(" ", scopes);

        // Google's authorization server only issues a refresh token when asked for offline access,
        // and its Workspace MCP servers sit behind that server as pre-registered (rung 1) rows.
        // Without this the connection silently dies when the first access token expires. Every
        // other server is required by RFC 6749 section 3.1 to ignore a parameter it does not know.
        query["access_type"] = "offline";

        return $"{authorizationEndpoint}?{query}";
    }

    public async Task<PluginOAuthTokenDto> ExchangeCodeAsync(
        Plugin plugin,
        string code,
        PluginOAuthStateDto flowState,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(flowState.CodeVerifier))
            throw new InvalidOperationException("The OAuth state carries no PKCE verifier, so the code cannot be exchanged.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["code_verifier"] = flowState.CodeVerifier,
            ["resource"] = ResourceIdentifier(plugin),
        };

        using var response = await SendTokenRequestAsync(plugin, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Token exchange failed for plugin '{plugin.PluginKey}' with {(int)response.StatusCode}: {Summarise(body)}");
        }

        var token = ParseTokenResponse(body);
        if (string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException($"Token response for plugin '{plugin.PluginKey}' carried no access token.");

        return token;
    }

    public async Task<PluginOAuthRefreshResultDto> RefreshAccessTokenAsync(
        Plugin plugin,
        string refreshToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("A refresh token is required.", nameof(refreshToken));

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["resource"] = ResourceIdentifier(plugin),
        };

        HttpResponseMessage response;
        try
        {
            response = await SendTokenRequestAsync(plugin, form, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Not proof the grant is dead, so the connection stays as it is. Ending it would cost
            // the user a browser round trip; retrying costs nothing.
            return PluginOAuthRefreshResultMapper.ProviderUnavailable(e.Message);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var token = ParseTokenResponse(body);
                return string.IsNullOrWhiteSpace(token.AccessToken)
                    ? PluginOAuthRefreshResultMapper.ProviderUnavailable("The refresh response carried no access token.")
                    : PluginOAuthRefreshResultMapper.Succeeded(token);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return PluginOAuthRefreshResultMapper.ProviderRateLimited(Summarise(body));

            // Only an explicit refusal of the grant itself ends the connection. Anything else -
            // a 500, a proxy error, an unparseable body - is transient by default.
            return IsGrantRejection(response.StatusCode, body)
                ? PluginOAuthRefreshResultMapper.GrantRejected(Summarise(body))
                : PluginOAuthRefreshResultMapper.ProviderUnavailable(Summarise(body));
        }
    }

    public async Task RevokeTokenAsync(Plugin plugin, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plugin.OAuthRevokeEndpoint)) return;

        var form = new Dictionary<string, string>
        {
            ["token"] = token,
        };

        ApplyClientAuthentication(plugin, form, out var authorizationHeader);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, plugin.OAuthRevokeEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };
            if (authorizationHeader is not null) request.Headers.Authorization = authorizationHeader;

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Revoking the token for plugin {PluginKey} returned {StatusCode}; the local disconnect still stands.",
                    plugin.PluginKey,
                    (int)response.StatusCode);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Disconnect is a local decision first. A provider having a bad minute must not block
            // a user from removing a plugin.
            _logger.LogInformation(e, "Revoking the token for plugin {PluginKey} failed.", plugin.PluginKey);
        }
    }

    // ---- request plumbing --------------------------------------------------------------------

    private Task<HttpResponseMessage> SendTokenRequestAsync(
        Plugin plugin,
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        var tokenEndpoint = Require(plugin.OAuthTokenEndpoint, plugin, "token endpoint");
        ApplyClientAuthentication(plugin, form, out var authorizationHeader);

        var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (authorizationHeader is not null) request.Headers.Authorization = authorizationHeader;

        return _httpClient.SendAsync(request, ct);
    }

    /// <summary>
    /// Applies whatever client authentication the registration ladder negotiated for this row.
    /// </summary>
    /// <remarks>
    /// The negotiated method is read from the row rather than guessed, which is the point of
    /// persisting it: a CIMD row that landed on <c>none</c> is a public client at that server, and
    /// sending a secret it does not have - or one the server would reject for a metadata-document
    /// client - turns a working flow into an opaque <c>invalid_client</c>.
    /// </remarks>
    private void ApplyClientAuthentication(
        Plugin plugin,
        Dictionary<string, string> form,
        out System.Net.Http.Headers.AuthenticationHeaderValue? authorizationHeader)
    {
        authorizationHeader = null;

        var clientId = Require(plugin.OAuthClientId, plugin, "client id");
        var method = plugin.OAuthTokenEndpointAuthMethod ?? PluginConstants.TokenEndpointAuthMethod.None;

        switch (method)
        {
            case PluginConstants.TokenEndpointAuthMethod.PrivateKeyJwt:
                var assertion = _signingKeys.CreateClientAssertion(
                    clientId,
                    Require(plugin.OAuthTokenEndpoint, plugin, "token endpoint"),
                    ClientAssertionLifetime);

                if (assertion is null)
                {
                    // The row negotiated private_key_jwt against a published document that promised
                    // a key, and the key is now gone. Failing here beats sending an unauthenticated
                    // request the server will reject for a reason that points nowhere.
                    throw new InvalidOperationException(
                        $"Plugin '{plugin.PluginKey}' negotiated private_key_jwt but no signing key is loaded.");
                }

                form["client_id"] = clientId;
                form["client_assertion_type"] = ClientAssertionType;
                form["client_assertion"] = assertion;
                break;

            case PluginConstants.TokenEndpointAuthMethod.ClientSecretBasic:
                var basicSecret = UnprotectSecret(plugin);
                authorizationHeader = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(basicSecret)}")));
                break;

            case PluginConstants.TokenEndpointAuthMethod.ClientSecretPost:
                form["client_id"] = clientId;
                form["client_secret"] = UnprotectSecret(plugin);
                break;

            default:
                // Public client: PKCE and the exact HTTPS redirect URI carry the security here.
                form["client_id"] = clientId;
                break;
        }
    }

    private string UnprotectSecret(Plugin plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.OAuthClientSecretEncrypted))
        {
            throw new InvalidOperationException(
                $"Plugin '{plugin.PluginKey}' negotiated {plugin.OAuthTokenEndpointAuthMethod} but stores no client secret.");
        }

        return _credentialProtector.Unprotect(plugin.OAuthClientSecretEncrypted);
    }

    // ---- parsing -----------------------------------------------------------------------------

    private static PluginOAuthTokenDto ParseTokenResponse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var accessToken = ReadString(root, "access_token") ?? string.Empty;
        var scope = ReadString(root, "scope") ?? string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? seconds
            : (int?)null;

        // The provider identity comes out of the id_token when one is returned, which is the whole
        // point of asking for it: the user consented moments ago, so making them go round a second
        // identity endpoint - or worse, sign in again - buys nothing.
        var (subject, email) = ReadIdentityClaims(ReadString(root, "id_token"));

        return new PluginOAuthTokenDto(
            subject,
            email,
            scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            accessToken,
            ReadString(root, "refresh_token"),
            expiresIn.HasValue ? DateTime.UtcNow.AddSeconds(expiresIn.Value) : null);
    }

    /// <summary>
    /// Reads <c>sub</c> and <c>email</c> out of an ID token without verifying its signature.
    /// </summary>
    /// <remarks>
    /// Deliberately unverified, and safe only because of where it comes from: the token arrived in
    /// the body of a direct, TLS-protected response to a request this client just made to an
    /// endpoint discovery validated. It never passes through the user agent, so there is no
    /// attacker in a position to substitute it.
    /// <para>
    /// These claims are used as a display label and a stable account key, never as an authorisation
    /// decision - the caller's own identity comes from their WarpTalk bearer token. If that ever
    /// changes, this must become a full validation with the issuer's JWKS.
    /// </para>
    /// </remarks>
    private static (string? Subject, string? Email) ReadIdentityClaims(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return (null, null);

        try
        {
            var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(idToken);
            return (
                token.TryGetClaim("sub", out var sub) ? sub.Value : null,
                token.TryGetClaim("email", out var email) ? email.Value : null);
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Only an explicit refusal of the grant ends a connection. <c>invalid_grant</c> is that
    /// refusal; <c>invalid_client</c> is about us, not the user's grant, and is worth retrying
    /// after a configuration fix rather than forcing a re-consent.
    /// </summary>
    private static bool IsGrantRejection(HttpStatusCode statusCode, string body)
    {
        if (statusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)) return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "error") == "invalid_grant";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Bounded so a provider's error page cannot end up in a log line or an exception.</summary>
    private static string Summarise(string body) =>
        body.Length <= 300 ? body : body[..300] + "...";

    // ---- small helpers -----------------------------------------------------------------------

    private static string CreateCodeVerifier() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string CreateCodeChallenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// The RFC 8707 <c>resource</c>: the canonical URI of the MCP server this token is for. The
    /// identical string must go on both legs or the server has no reason to treat them as one flow.
    /// </summary>
    private static string ResourceIdentifier(Plugin plugin)
    {
        var url = Require(plugin.McpServerUrl, plugin, "MCP server URL");
        var canonical = new Uri(url).GetLeftPart(UriPartial.Path);
        return canonical.Length > 1 && canonical.EndsWith('/') ? canonical.TrimEnd('/') : canonical;
    }

    private static string IssuerOf(Plugin plugin) =>
        new Uri(plugin.OAuthAuthorizationEndpoint!).GetLeftPart(UriPartial.Authority);

    private static string Require(string? value, Plugin plugin, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"Plugin '{plugin.PluginKey}' has no {what}; provisioning must run before the OAuth flow.")
            : value;
}
