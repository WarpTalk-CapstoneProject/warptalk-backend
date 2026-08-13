using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces.Security;

namespace WarpTalk.AuthService.Infrastructure.Security;

/// <summary>
/// Turns a Google credential supplied by an untrusted caller into a verified identity.
///
/// The rule this class exists to enforce: <b>we only ever trust a credential that Google
/// minted for the WarpTalk OAuth client.</b>
///
/// This used to be broken in a way that allowed silent account takeover. The ID-token branch
/// was correct, but it was wrapped in a bare <c>catch (Exception)</c> that fell through to
/// Google's userinfo endpoint with whatever string the caller sent. userinfo honours *any*
/// valid Google access token regardless of which OAuth client minted it, and it does not tell
/// you who that client was. So an attacker who registered their own unrelated Google app and
/// got a user to sign into *that* app held a token which WarpTalk would exchange for a full
/// session on the victim's email — no password, no phishing of WarpTalk itself, nothing the
/// victim could notice.
///
/// Current web clients send Google ID tokens, but the access-token branch is kept temporarily
/// for older deployed clients during rollout. It is gated on <c>tokeninfo</c>, which unlike
/// userinfo *does* report the audience the token was issued to. A token from any other client
/// is rejected.
///
/// The correct end state is for the frontend to send an ID token and for the access-token
/// branch to be removed entirely.
/// </summary>
public class GoogleTokenVerifier : IGoogleTokenVerifier
{
    /// <summary>Reports the OAuth client a token was issued to. userinfo does not.</summary>
    public const string TokenInfoEndpoint = "https://oauth2.googleapis.com/tokeninfo";

    public const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";

    private readonly string _clientId;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleTokenVerifier> _logger;

    public GoogleTokenVerifier(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleTokenVerifier> logger)
    {
        var clientId = configuration["Authentication:Google:ClientId"]?.Trim();
        if (string.IsNullOrWhiteSpace(clientId)
            || string.Equals(clientId, "CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Google ClientId is not configured.");
        }

        _clientId = clientId;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<GoogleAuthPayload?> VerifyGoogleTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        // 1. ID token. This is the path we want every caller on: the signature, the issuer and
        //    the audience are all checked locally against Google's published keys.
        if (LooksLikeJwt(idToken))
        {
            try
            {
                var settings = new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { _clientId }
                };

                var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
                if (payload is not null)
                {
                    return new GoogleAuthPayload(
                        payload.Subject,
                        payload.Email,
                        payload.Name,
                        payload.Picture,
                        payload.EmailVerified);
                }
            }
            catch (InvalidJwtException ex)
            {
                // A JWT-shaped string that does not validate is a rejected credential, full stop.
                // It must NOT fall through to the access-token branch: an attacker who can make
                // ID-token validation fail would otherwise get a second, weaker attempt.
                _logger.LogWarning(ex, "Google ID token failed validation.");
                return null;
            }

            // Deliberately no bare catch. A transport or configuration fault must surface as a
            // fault, not silently downgrade the caller onto a weaker verification path.
            return null;
        }

        // 2. Opaque OAuth2 access token. Kept temporarily so clients deployed before the
        // ID-token contract update keep working during rollout.
        return await VerifyAccessTokenAsync(idToken, ct);
    }

    /// <summary>
    /// Accepts an opaque access token ONLY after tokeninfo confirms Google issued it to our
    /// OAuth client. Without that check any Google app's token would be accepted.
    /// </summary>
    private async Task<GoogleAuthPayload?> VerifyAccessTokenAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient(nameof(GoogleTokenVerifier));

            // tokeninfo takes the token as a query parameter; that is Google's contract for this
            // endpoint, and the request goes over TLS to the token's own issuer.
            var tokenInfoUrl = $"{TokenInfoEndpoint}?access_token={Uri.EscapeDataString(accessToken)}";
            using var tokenInfoResponse = await httpClient.GetAsync(tokenInfoUrl, ct);
            if (!tokenInfoResponse.IsSuccessStatusCode)
            {
                // WT-361 — 4xx and 5xx from tokeninfo mean opposite things and must not share
                // an answer. 4xx is Google looking at the token and refusing it: that is a
                // verdict, and null (which becomes "invalid token") is correct. 5xx is Google
                // being unable to answer, which says nothing about the token at all — swallowing
                // it told the user their perfectly good credential was rejected.
                if ((int)tokenInfoResponse.StatusCode >= 500)
                {
                    throw new HttpRequestException(
                        $"Google tokeninfo returned {(int)tokenInfoResponse.StatusCode}; "
                        + "the token could not be verified.");
                }

                _logger.LogWarning(
                    "Google tokeninfo rejected an access token with status {StatusCode}.",
                    (int)tokenInfoResponse.StatusCode);
                return null;
            }

            using var tokenInfo = JsonDocument.Parse(await tokenInfoResponse.Content.ReadAsStringAsync(ct));
            var tokenInfoRoot = tokenInfo.RootElement;

            // "aud" is the client the token was issued to; "azp" is the authorised party. Google
            // populates both for access tokens and either one matching is proof of provenance.
            var audience = ReadString(tokenInfoRoot, "aud");
            var authorizedParty = ReadString(tokenInfoRoot, "azp");

            if (!IsOurClient(audience) && !IsOurClient(authorizedParty))
            {
                // This is the account-takeover signature. Log it loudly: a legitimate WarpTalk
                // client can never reach here.
                //
                // WT-361 — the values are NAMED now, and that is a deliberate change. This
                // message used to say only "reported aud/azp that did not match", which reads as
                // an attack and is far more often a deployment mistake: the web bundle is built
                // with NEXT_PUBLIC_GOOGLE_CLIENT_ID and the auth service reads GOOGLE_CLIENT_ID
                // from the host environment, two values nothing keeps in step. Google sign-in was
                // reported broken in production with nothing to go on but a bare 400, and this
                // log line was the one place that could have said why and did not.
                //
                // An OAuth client id is not a secret — it ships inside the public JavaScript
                // bundle and is visible to anyone who opens devtools. Printing it costs nothing
                // and turns an undiagnosable outage into one line.
                _logger.LogWarning(
                    "Rejected a Google access token issued to a foreign OAuth client. "
                    + "Expected client id {ExpectedClientId}; token reported aud={TokenAudience} azp={TokenAuthorizedParty}. "
                    + "If the reported values look like WarpTalk's own web client, the auth "
                    + "service's GOOGLE_CLIENT_ID and the web bundle's NEXT_PUBLIC_GOOGLE_CLIENT_ID "
                    + "have drifted apart.",
                    _clientId,
                    audience ?? "(absent)",
                    authorizedParty ?? "(absent)");
                return null;
            }

            var tokenInfoSubject = ReadString(tokenInfoRoot, "sub");
            if (string.IsNullOrEmpty(tokenInfoSubject)) return null;

            // tokeninfo has established provenance. userinfo is now safe to call, purely to pick
            // up the profile fields (name, picture) tokeninfo does not carry.
            using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var userInfoResponse = await httpClient.SendAsync(userInfoRequest, ct);
            if (!userInfoResponse.IsSuccessStatusCode) return null;

            using var userInfo = JsonDocument.Parse(await userInfoResponse.Content.ReadAsStringAsync(ct));
            var userInfoRoot = userInfo.RootElement;

            var subject = ReadString(userInfoRoot, "sub");
            var email = ReadString(userInfoRoot, "email");

            if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email)) return null;

            // The identity we act on must be the identity whose provenance we just proved.
            if (!string.Equals(subject, tokenInfoSubject, StringComparison.Ordinal))
            {
                _logger.LogWarning("Google tokeninfo and userinfo disagreed on the subject; rejecting.");
                return null;
            }

            var name = ReadString(userInfoRoot, "name");
            var picture = ReadString(userInfoRoot, "picture");
            var emailVerified = userInfoRoot.TryGetProperty("email_verified", out var ev)
                && ev.ValueKind == JsonValueKind.True;

            return new GoogleAuthPayload(subject, email, name, picture, emailVerified);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // WT-361 — rethrown, not swallowed. This used to return null, and null means
            // "verified: not our client" everywhere upstream — so a DNS hiccup or a Google
            // outage was reported to the user as an invalid credential, and to the operator as
            // a 400 that looks like the caller's fault.
            //
            // GoogleLoginAsync's catch-all turns this into InternalServerError, which
            // GoogleAuthController now answers with 503: "we could not check", which is both
            // true and retryable.
            _logger.LogWarning(ex, "Could not reach Google to verify an access token.");
            throw;
        }
    }

    private bool IsOurClient(string? candidate) =>
        !string.IsNullOrEmpty(candidate) && string.Equals(candidate, _clientId, StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// A Google ID token is a JWT. Checking the shape first keeps opaque access tokens from
    /// making a pointless round trip to fetch Google's signing certificates.
    /// </summary>
    private static bool LooksLikeJwt(string token) => token.Split('.').Length == 3;
}
