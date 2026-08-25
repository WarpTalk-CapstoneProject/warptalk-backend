using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Infrastructure.OAuth;

namespace WarpTalk.AssistantService.Tests.Plugins;

public class GoogleWorkspaceOAuthClientTests
{
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

        var token = await sut.RefreshAccessTokenAsync(GoogleWorkspacePlugin(), "stored-refresh-token");

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

        var token = await sut.RefreshAccessTokenAsync(GoogleWorkspacePlugin(), "stored-refresh-token");

        Assert.Equal("fresh-access-token", token.AccessToken);
        Assert.Null(token.RefreshToken);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_Throws_WhenGoogleRejectsTheGrant()
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant"}"""),
            })));
        var sut = CreateSut(httpClient);

        await Assert.ThrowsAnyAsync<Exception>(
            () => sut.RefreshAccessTokenAsync(GoogleWorkspacePlugin(), "revoked-refresh-token"));
    }

    private static GoogleWorkspaceOAuthClient CreateSut(HttpClient httpClient)
    {
        return new GoogleWorkspaceOAuthClient(
            httpClient,
            Options.Create(new GoogleWorkspaceOAuthOptions
            {
                ClientId = "test-client",
                ClientSecret = "test-secret",
                TokenEndpoint = "https://oauth2.google.test/token",
            }));
    }

    private static Plugin GoogleWorkspacePlugin()
    {
        return new Plugin
        {
            Id = Guid.NewGuid(),
            PluginKey = PluginConstants.GoogleWorkspace,
            Label = "Google Workspace",
            Description = "Work across Google Drive and Calendar.",
            Provider = "google",
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
