using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AuthService.Infrastructure.Security;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// Guards the account-takeover fix in <see cref="GoogleTokenVerifier"/>.
///
/// The defect: the verifier accepted any string Google's userinfo endpoint would answer for.
/// userinfo honours a valid access token from *any* OAuth client, so a token minted by an
/// attacker's own unrelated Google app bought a WarpTalk session for whatever email that token
/// belonged to. These tests exist to make that specific exchange fail.
/// </summary>
public class GoogleTokenVerifierTests
{
    private const string OurClientId = "warptalk-test-client-id.apps.googleusercontent.com";
    private const string AttackerClientId = "attacker-owned-app.apps.googleusercontent.com";

    /// <summary>An opaque (non-JWT) token, the shape Google issues for OAuth2 access tokens.</summary>
    private const string OpaqueAccessToken = "ya29.fake-opaque-access-token-for-tests";

    [Fact]
    public async Task Rejects_access_token_minted_by_a_different_google_client()
    {
        // The whole attack in one arrangement: userinfo would happily identify the victim,
        // but the token was issued to an app the attacker controls.
        var handler = new StubHandler
        {
            TokenInfoJson = TokenInfo(AttackerClientId, subject: "victim-google-subject"),
            UserInfoJson = UserInfo("victim-google-subject", "victim@warptalk.test")
        };

        var result = await CreateVerifier(handler).VerifyGoogleTokenAsync(OpaqueAccessToken);

        Assert.Null(result);

        // And the identity must never have been fetched — provenance is checked first.
        Assert.DoesNotContain(handler.RequestedUrls, url => url.StartsWith(GoogleTokenVerifier.UserInfoEndpoint, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accepts_access_token_minted_by_our_own_google_client()
    {
        var handler = new StubHandler
        {
            TokenInfoJson = TokenInfo(OurClientId, subject: "real-google-subject"),
            UserInfoJson = UserInfo("real-google-subject", "user@warptalk.test")
        };

        var result = await CreateVerifier(handler).VerifyGoogleTokenAsync(OpaqueAccessToken);

        Assert.NotNull(result);
        Assert.Equal("real-google-subject", result!.Subject);
        Assert.Equal("user@warptalk.test", result.Email);
        Assert.True(result.EmailVerified);
    }

    [Fact]
    public async Task Accepts_access_token_when_only_azp_identifies_our_client()
    {
        var handler = new StubHandler
        {
            TokenInfoJson =
                $$"""{"azp":"{{OurClientId}}","aud":"some-google-api-audience","sub":"real-google-subject","expires_in":"3599"}""",
            UserInfoJson = UserInfo("real-google-subject", "user@warptalk.test")
        };

        var result = await CreateVerifier(handler).VerifyGoogleTokenAsync(OpaqueAccessToken);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Rejects_when_tokeninfo_refuses_the_token()
    {
        var handler = new StubHandler
        {
            TokenInfoStatus = HttpStatusCode.BadRequest,
            UserInfoJson = UserInfo("victim-google-subject", "victim@warptalk.test")
        };

        Assert.Null(await CreateVerifier(handler).VerifyGoogleTokenAsync(OpaqueAccessToken));
    }

    [Fact]
    public async Task Rejects_when_tokeninfo_and_userinfo_disagree_on_the_subject()
    {
        var handler = new StubHandler
        {
            TokenInfoJson = TokenInfo(OurClientId, subject: "subject-a"),
            UserInfoJson = UserInfo("subject-b", "someone.else@warptalk.test")
        };

        Assert.Null(await CreateVerifier(handler).VerifyGoogleTokenAsync(OpaqueAccessToken));
    }

    /// <summary>
    /// A JWT-shaped credential that fails ID-token validation must be rejected outright. It used
    /// to fall through to the access-token path, which handed a failed credential a second try.
    /// </summary>
    [Fact]
    public async Task Rejects_a_jwt_shaped_token_that_fails_id_token_validation_without_falling_back()
    {
        var handler = new StubHandler
        {
            TokenInfoJson = TokenInfo(OurClientId, subject: "victim-google-subject"),
            UserInfoJson = UserInfo("victim-google-subject", "victim@warptalk.test")
        };

        var forgedIdToken = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ2aWN0aW0ifQ.not-a-real-signature";

        Assert.Null(await CreateVerifier(handler).VerifyGoogleTokenAsync(forgedIdToken));
        Assert.Empty(handler.RequestedUrls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_blank_tokens(string token)
    {
        Assert.Null(await CreateVerifier(new StubHandler()).VerifyGoogleTokenAsync(token));
    }

    private static GoogleTokenVerifier CreateVerifier(StubHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Google:ClientId"] = OurClientId
            })
            .Build();

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new GoogleTokenVerifier(
            configuration,
            factory,
            NullLogger<GoogleTokenVerifier>.Instance);
    }

    private static string TokenInfo(string audience, string subject) =>
        $$"""{"aud":"{{audience}}","azp":"{{audience}}","sub":"{{subject}}","expires_in":"3599","email_verified":"true"}""";

    private static string UserInfo(string subject, string email) =>
        $$"""{"sub":"{{subject}}","email":"{{email}}","email_verified":true,"name":"Test User","picture":"https://example.test/avatar.png"}""";

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();

        public HttpStatusCode TokenInfoStatus { get; set; } = HttpStatusCode.OK;
        public string TokenInfoJson { get; set; } = "{}";
        public string UserInfoJson { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);

            if (url.StartsWith(GoogleTokenVerifier.TokenInfoEndpoint, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(TokenInfoStatus)
                {
                    Content = new StringContent(TokenInfoJson)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(UserInfoJson)
            });
        }
    }
}
