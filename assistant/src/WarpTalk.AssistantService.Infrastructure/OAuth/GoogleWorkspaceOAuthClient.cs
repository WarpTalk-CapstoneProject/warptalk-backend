using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Infrastructure.OAuth;

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

    public string BuildAuthorizationUrl(Plugin plugin, IReadOnlyList<string> scopes, string state)
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
