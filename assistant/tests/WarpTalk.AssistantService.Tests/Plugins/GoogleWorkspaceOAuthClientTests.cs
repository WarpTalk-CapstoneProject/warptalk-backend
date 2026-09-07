using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Infrastructure.OAuth;

namespace WarpTalk.AssistantService.Tests.Plugins;

public class GoogleWorkspaceOAuthClientTests
{
    private const string GoogleDriveKey = "google_drive";

    [Fact]
    public async Task RefreshAccessTokenAsync_PostsRefreshTokenGrant_AndReturnsNewAccessToken()
    {
        string? capturedBody = null;
        HttpRequestMessage? capturedRequest = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["access_token"] = "fresh-access-token",
                    ["expires_in"] = 3599,
                    ["refresh_token"] = "rotated-refresh-token",
                }),
            };
        }));
        var sut = CreateSut(httpClient);

        var refresh = await sut.RefreshAccessTokenAsync(GoogleDrivePlugin(), "stored-refresh-token");

        Assert.Equal(PluginOAuthRefreshOutcome.Succeeded, refresh.Outcome);
        var token = refresh.Token!;
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://oauth2.google.test/token", capturedRequest.RequestUri!.ToString());
        Assert.Contains("grant_type=refresh_token", capturedBody);
        Assert.Contains("refresh_token=stored-refresh-token", capturedBody);
        Assert.Contains("client_id=test-client", capturedBody);
        Assert.Contains("client_secret=test-secret", capturedBody);
        Assert.DoesNotContain("code=", capturedBody);
        Assert.Equal("fresh-access-token", token.AccessToken);
        Assert.Equal("rotated-refresh-token", token.RefreshToken);
        Assert.NotNull(token.AccessTokenExpiresAt);
        Assert.True(token.AccessTokenExpiresAt > DateTime.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_ReturnsNullRefreshToken_WhenGoogleOmitsIt()
    {
        // Google only returns refresh_token on the initial authorization_code exchange, so the
        // refresh response normally carries none - the caller has to keep the stored one.
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["access_token"] = "fresh-access-token",
                    ["expires_in"] = 3599,
                }),
            })));
        var sut = CreateSut(httpClient);

        var refresh = await sut.RefreshAccessTokenAsync(GoogleDrivePlugin(), "stored-refresh-token");

        Assert.Equal(PluginOAuthRefreshOutcome.Succeeded, refresh.Outcome);
        Assert.Equal("fresh-access-token", refresh.Token!.AccessToken);
        Assert.Null(refresh.Token.RefreshToken);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_ReportsGrantRejected_WhenGoogleAnswersInvalidGrant()
    {
        // The only answer that proves the stored grant is dead: revoked access, a password change,
        // or a token pruned for age all surface as 400 invalid_grant.
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant"}"""),
            })));
        var sut = CreateSut(httpClient);

        var refresh = await sut.RefreshAccessTokenAsync(GoogleDrivePlugin(), "revoked-refresh-token");

        Assert.Equal(PluginOAuthRefreshOutcome.GrantRejected, refresh.Outcome);
        Assert.Null(refresh.Token);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_ReportsProviderUnavailable_WhenGoogleReturnsServerError()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var sut = CreateSut(httpClient);

        var refresh = await sut.RefreshAccessTokenAsync(GoogleDrivePlugin(), "stored-refresh-token");

        Assert.Equal(PluginOAuthRefreshOutcome.ProviderUnavailable, refresh.Outcome);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_ReportsProviderRateLimited_WhenGoogleReturns429()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("""{"error":"rateLimitExceeded"}"""),
            })));
        var sut = CreateSut(httpClient);

        var refresh = await sut.RefreshAccessTokenAsync(GoogleDrivePlugin(), "stored-refresh-token");

        Assert.Equal(PluginOAuthRefreshOutcome.ProviderRateLimited, refresh.Outcome);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_ReportsProviderUnavailable_WhenGoogleReturnsNonInvalidGrantBadRequest()
    {
        // invalid_client means our own client id/secret is wrong. Reading that as a dead user grant
        // would turn one bad config push into a mass re-consent, so it stays transient.
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_client"}"""),
            })));
        var sut = CreateSut(httpClient);

        var refresh = await sut.RefreshAccessTokenAsync(GoogleDrivePlugin(), "stored-refresh-token");

        Assert.Equal(PluginOAuthRefreshOutcome.ProviderUnavailable, refresh.Outcome);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_ReportsProviderUnavailable_WhenTheRequestNeverReachesGoogle()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("No such host is known.")));
        var sut = CreateSut(httpClient);

        var refresh = await sut.RefreshAccessTokenAsync(GoogleDrivePlugin(), "stored-refresh-token");

        Assert.Equal(PluginOAuthRefreshOutcome.ProviderUnavailable, refresh.Outcome);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_ReportsProviderUnavailable_WhenGoogleAnswersUnparseableBody()
    {
        // Google's token endpoint sits behind proxies that can answer HTML on a bad day.
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html><body>502 Bad Gateway</body></html>"),
            })));
        var sut = CreateSut(httpClient);

        var refresh = await sut.RefreshAccessTokenAsync(GoogleDrivePlugin(), "stored-refresh-token");

        Assert.Equal(PluginOAuthRefreshOutcome.ProviderUnavailable, refresh.Outcome);
    }

    [Fact]
    public async Task RevokeTokenAsync_PostsTokenToGoogleRevokeEndpoint()
    {
        string? capturedBody = null;
        HttpRequestMessage? capturedRequest = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var sut = CreateSut(httpClient);

        await sut.RevokeTokenAsync(GoogleDrivePlugin(), "stored-refresh-token");

        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://oauth2.google.test/revoke", capturedRequest.RequestUri!.ToString());
        Assert.Equal("token=stored-refresh-token", capturedBody);
    }

    // ---- WT-646: the invariant assertion is on Provider, not on the plugin key ----------------

    [Theory]
    [InlineData("google_drive")]
    [InlineData("google_calendar")]
    [InlineData("google_meet")]
    public void BuildAuthorizationUrl_ServesEveryGoogleCatalogRow(string pluginKey)
    {
        // Before WT-646 this compared the key against 'google_workspace', so after the split all
        // three of these threw and no Google plugin could be connected at all. It also stands in
        // for development's BuildAuthorizationUrl_IncludesClientId_WhenConfigured, which asserted
        // the same configured client_id against the one key that no longer exists.
        var sut = CreateSut(new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));

        var url = sut.BuildAuthorizationUrl(
            GooglePlugin(pluginKey),
            ["https://www.googleapis.com/auth/calendar.events"],
            "state-token",
            new PluginOAuthStateDto(Guid.NewGuid(), pluginKey));

        Assert.Contains("state=state-token", url, StringComparison.Ordinal);
        Assert.Contains("client_id=test-client", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeCodeAsync_RefusesAPluginFromAnotherProvider()
    {
        // The guard that matters: this client posts to Google's token endpoint with Google's
        // client secret, so another provider's authorization code must never reach it.
        var sut = CreateSut(new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));
        var foreign = GooglePlugin("remote_app");
        foreign.Provider = "remote_app";

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.ExchangeCodeAsync(foreign, "code", new PluginOAuthStateDto(Guid.NewGuid(), "remote_app")));
    }

    [Fact]
    public void BuildAuthorizationUrl_Throws_WhenClientIdIsNotConfigured()
    {
        // The production bug this guards: app.compose.yml passed GOOGLE_WORKSPACE_CLIENT_ID with a
        // `:-` default and nothing ever defined the variable, so ClientId bound to "" - the option's
        // own default. The service started healthy and the flow died on Google's consent page with
        // "Missing required parameter: client_id", which named neither the key nor the service.
        var sut = CreateSut(new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))), clientId: "");

        var ex = Assert.Throws<InvalidOperationException>(() => sut.BuildAuthorizationUrl(
            GoogleDrivePlugin(),
            [],
            "opaque-state",
            new PluginOAuthStateDto(Guid.NewGuid(), GoogleDriveKey)));

        // The message has to carry the environment key, because that is the part a reader of the
        // logs cannot derive from anything else.
        Assert.Contains("GOOGLE_WORKSPACE_CLIENT_ID", ex.Message);
        Assert.Contains("Plugins__GoogleWorkspace__OAuth__ClientId", ex.Message);
    }

    [Fact]
    public async Task ExchangeCodeAsync_Throws_WhenClientSecretIsNotConfigured()
    {
        // Same misconfiguration, one leg later. Without the guard this reaches Google and comes
        // back as a bare 401 invalid_client, which reads like a revoked app rather than a missing
        // deployment variable.
        var reached = false;
        var sut = CreateSut(
            new HttpClient(new StubHttpMessageHandler(_ =>
            {
                reached = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })),
            clientSecret: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExchangeCodeAsync(
            GoogleDrivePlugin(),
            "authorization-code",
            new PluginOAuthStateDto(Guid.NewGuid(), GoogleDriveKey)));

        Assert.Contains("GOOGLE_WORKSPACE_CLIENT_SECRET", ex.Message);
        Assert.False(reached);
    }

    private static Plugin GooglePlugin(string pluginKey)
    {
        return new Plugin
        {
            Id = Guid.NewGuid(),
            PluginKey = pluginKey,
            Label = pluginKey,
            Description = pluginKey,
            Provider = PluginConstants.Providers.Google,
            IsActive = true,
            RequiredScopesJson = "[]",
            ToolsJson = "[]",
        };
    }

    private static GoogleWorkspaceOAuthClient CreateSut(
        HttpClient httpClient,
        string clientId = "test-client",
        string clientSecret = "test-secret")
    {
        return new GoogleWorkspaceOAuthClient(
            httpClient,
            Options.Create(new GoogleWorkspaceOAuthOptions
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
                TokenEndpoint = "https://oauth2.google.test/token",
                RevokeEndpoint = "https://oauth2.google.test/revoke",
            }));
    }

    private static Plugin GoogleDrivePlugin()
    {
        return new Plugin
        {
            Id = Guid.NewGuid(),
            PluginKey = GoogleDriveKey,
            Label = "Google Drive",
            Description = "Search your Google Drive and read the contents of a file.",
            Provider = PluginConstants.Providers.Google,
            IsActive = true,
            RequiredScopesJson = "[]",
            ToolsJson = "[]",
        };
    }

    private class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
