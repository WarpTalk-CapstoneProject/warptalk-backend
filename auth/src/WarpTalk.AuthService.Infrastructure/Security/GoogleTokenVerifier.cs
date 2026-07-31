using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces.Security;

namespace WarpTalk.AuthService.Infrastructure.Security;

public class GoogleTokenVerifier : IGoogleTokenVerifier
{
    private readonly string _clientId;

    public GoogleTokenVerifier(IConfiguration configuration)
    {
        _clientId = configuration["Authentication:Google:ClientId"]
            ?? configuration["GOOGLE_CLIENT_ID"]
            ?? throw new InvalidOperationException("Google ClientId is not configured.");
    }

    public async Task<GoogleAuthPayload?> VerifyGoogleTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        // 1. Try validating as Google JWT ID Token
        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _clientId }
            };

            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
            if (payload != null)
            {
                return new GoogleAuthPayload(
                    payload.Subject,
                    payload.Email,
                    payload.Name,
                    payload.Picture,
                    payload.EmailVerified
                );
            }
        }
        catch (InvalidJwtException)
        {
            // Fallthrough to UserInfo endpoint
        }
        catch (Exception)
        {
            // Fallthrough to UserInfo endpoint
        }

        // 2. Fallback: Validate as OAuth2 Access Token via Google UserInfo API
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            var response = await httpClient.GetAsync("https://www.googleapis.com/oauth2/v3/userinfo", ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sub = root.GetProperty("sub").GetString() ?? "";
            var email = root.GetProperty("email").GetString() ?? "";
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : "";
            var picture = root.TryGetProperty("picture", out var p) ? p.GetString() : "";
            var emailVerified = root.TryGetProperty("email_verified", out var ev) && ev.GetBoolean();

            if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email)) return null;

            return new GoogleAuthPayload(sub, email, name, picture, emailVerified);
        }
        catch
        {
            return null;
        }
    }
}
