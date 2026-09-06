using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Infrastructure.OAuth;

/// <summary>
/// OAuth for the one provider that has no official remote MCP server for Drive/Calendar, so it
/// keeps a hand-written client instead of going through the generic MCP path.
/// </summary>
/// <remarks>
/// The <c>PluginKey</c> checks in every method are <em>not</em> dispatch - <c>IPluginProviderResolver</c>
/// does that, keyed on <c>Plugin.Kind</c>. They are invariant assertions, and they must stay: this
/// client reads its endpoints and client credentials from <c>GoogleWorkspaceOAuthOptions</c>, so if a
/// second <c>native</c> plugin were ever routed here it would silently send that provider's
/// authorization code to Google's token endpoint. Failing loudly is the only safe answer.
/// </remarks>
public class GoogleWorkspaceOAuthClient : IPluginOAuthClient
{
    private static readonly string[] IdentityScopes = ["openid", "email", "profile"];

    private readonly HttpClient _httpClient;
    private readonly GoogleWorkspaceOAuthOptions _options;

    public GoogleWorkspaceOAuthClient(HttpClient httpClient, IOptions<GoogleWorkspaceOAuthOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    /// <summary>
    /// Google's flow carries nothing beyond the identity already in the state: this client is a
    /// confidential client with a fixed redirect URI, so there is no PKCE verifier to remember.
    /// </summary>
    public PluginOAuthStateDto PrepareState(Plugin plugin, PluginOAuthStateDto state) => state;

    public string BuildAuthorizationUrl(
        Plugin plugin,
        IReadOnlyList<string> scopes,
        string state,
        PluginOAuthStateDto flowState)
    {
        if (!string.Equals(plugin.PluginKey, PluginConstants.GoogleWorkspace, StringComparison.Ordinal))
            throw new NotSupportedException($"OAuth is not configured for plugin '{plugin.PluginKey}'.");

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = _options.ClientId;
        query["redirect_uri"] = _options.RedirectUri;
        query["response_type"] = "code";
        query["scope"] = string.Join(" ", IdentityScopes.Concat(scopes).Distinct(StringComparer.Ordinal));
        query["state"] = state;
        query["access_type"] = "offline";
        query["prompt"] = "consent";
        query["include_granted_scopes"] = "true";
        return $"{_options.AuthorizationEndpoint}?{query}";
    }

    public async Task<PluginOAuthTokenDto> ExchangeCodeAsync(
        Plugin plugin,
        string code,
        PluginOAuthStateDto flowState,
        CancellationToken ct = default)
    {
        if (!string.Equals(plugin.PluginKey, PluginConstants.GoogleWorkspace, StringComparison.Ordinal))
            throw new NotSupportedException($"OAuth is not configured for plugin '{plugin.PluginKey}'.");

        var response = await _httpClient.PostAsync(
            _options.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = _options.RedirectUri,
            }),
            ct);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Google OAuth token response was empty.");
        if (string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException("Google OAuth token response did not include an access token.");

        var profile = await GetUserInfoAsync(token.AccessToken, ct);
        var grantedScopes = (token.Scope ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        return new PluginOAuthTokenDto(
            profile?.Subject,
            profile?.Email,
            grantedScopes,
            token.AccessToken,
            token.RefreshToken,
            token.ExpiresIn.HasValue ? DateTime.UtcNow.AddSeconds(token.ExpiresIn.Value) : null);
    }

    public async Task<PluginOAuthRefreshResultDto> RefreshAccessTokenAsync(
        Plugin plugin,
        string refreshToken,
        CancellationToken ct = default)
    {
        if (!string.Equals(plugin.PluginKey, PluginConstants.GoogleWorkspace, StringComparison.Ordinal))
            throw new NotSupportedException($"OAuth is not configured for plugin '{plugin.PluginKey}'.");

        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("A refresh token is required to refresh Google OAuth credentials.", nameof(refreshToken));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(
                _options.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token",
                }),
                ct);
        }
        catch (HttpRequestException ex)
        {
            // Never reached Google at all - DNS, TLS, connection reset. Says nothing about the grant.
            return PluginOAuthRefreshResultMapper.ProviderUnavailable($"Google token endpoint unreachable: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as TaskCanceledException; the caller's
            // cancellation is a different thing and must keep propagating.
            return PluginOAuthRefreshResultMapper.ProviderUnavailable($"Google token endpoint timed out: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return await ClassifyRefreshFailureAsync(response, ct);

            GoogleTokenResponse? token;
            try
            {
                token = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return PluginOAuthRefreshResultMapper.ProviderUnavailable($"Google refresh response was unreadable: {ex.Message}");
            }

            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                // A 200 with no access token is Google misbehaving, not Google refusing the grant.
                return PluginOAuthRefreshResultMapper.ProviderUnavailable("Google refresh response did not include an access token.");

            var grantedScopes = (token.Scope ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            // No user-info round trip here: the identity behind the grant cannot change on a refresh,
            // and RefreshToken stays null when Google omits it so the caller keeps the stored one.
            return PluginOAuthRefreshResultMapper.Succeeded(new PluginOAuthTokenDto(
                null,
                null,
                grantedScopes,
                token.AccessToken,
                token.RefreshToken,
                token.ExpiresIn.HasValue ? DateTime.UtcNow.AddSeconds(token.ExpiresIn.Value) : null));
        }
    }

    public async Task RevokeTokenAsync(
        Plugin plugin,
        string token,
        CancellationToken ct = default)
    {
        if (!string.Equals(plugin.PluginKey, PluginConstants.GoogleWorkspace, StringComparison.Ordinal))
            throw new NotSupportedException($"OAuth is not configured for plugin '{plugin.PluginKey}'.");

        if (string.IsNullOrWhiteSpace(token))
            return;

        using var response = await _httpClient.PostAsync(
            _options.RevokeEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = token,
            }),
            ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Turns Google's answer into the one distinction the caller needs: is this grant dead, or was
    /// this just a bad minute?
    /// </summary>
    /// <remarks>
    /// Only <c>invalid_grant</c> ends a connection. Google returns it (with a 400) when the user
    /// revoked access, changed their password, or the token was pruned for age - all of which need
    /// a fresh consent. Every other rejection is deliberately treated as transient, including
    /// <c>invalid_client</c>: that one means <em>our</em> client id/secret is wrong, and expiring
    /// every user's connection over a deployment misconfiguration would turn one bad config push
    /// into a mass re-consent.
    /// </remarks>
    private static async Task<PluginOAuthRefreshResultDto> ClassifyRefreshFailureAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = await ReadBodySafelyAsync(response, ct);
        var providerError = ExtractErrorCode(body);
        var detail = $"Google token endpoint returned {(int)response.StatusCode}"
            + (string.IsNullOrWhiteSpace(providerError) ? "." : $" ({providerError}).");

        if (string.Equals(providerError, "invalid_grant", StringComparison.OrdinalIgnoreCase))
            return PluginOAuthRefreshResultMapper.GrantRejected(detail);

        if ((int)response.StatusCode == 429)
            return PluginOAuthRefreshResultMapper.ProviderRateLimited(detail);

        return PluginOAuthRefreshResultMapper.ProviderUnavailable(detail);
    }

    private static async Task<string?> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string? ExtractErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // Google fronts its token endpoint with proxies that can answer HTML on a bad day.
            return null;
        }
    }

    private async Task<GoogleUserInfoResponse?> GetUserInfoAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<GoogleUserInfoResponse>(cancellationToken: ct);
    }

    private class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }

    private class GoogleUserInfoResponse
    {
        [JsonPropertyName("sub")]
        public string? Subject { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }
}
