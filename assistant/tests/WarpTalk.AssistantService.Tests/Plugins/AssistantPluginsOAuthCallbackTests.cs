using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The OAuth callbacks are the only actions in the product whose caller is a human's browser rather
/// than the frontend, so what they return is a page the user lands on. These pin that every way a
/// consent can end - including the ways it ends badly - lands them back in the app with the outcome
/// in the query string.
/// </summary>
/// <remarks>
/// Three failures motivated the set. The actions used to test <c>error</c> only for emptiness, so a
/// user pressing Cancel had their empty code sent to Google as if it were real. Nothing caught an
/// exception, so a 429 mid-redirect ended on a raw API error page - the exact thing the actions'
/// own doc comment says must never happen. And a consent given in a system browser or a second tab
/// had no way back to the surface that asked for it, which is why a completed callback goes through
/// <c>/connect/{provider}/callback</c> rather than straight to the plugins page.
/// </remarks>
public class AssistantPluginsOAuthCallbackTests
{
    private const string AppBaseUrl = "https://app.warptalk.test";
    private const string PluginsPage = $"{AppBaseUrl}/settings/plugins";
    private const string GoogleConnectPage = $"{AppBaseUrl}/connect/google/callback";
    private const string GoogleDriveKey = "google_drive";
    private const string RetiredWorkspaceKey = "google_workspace";
    private const string CorrelationId = "corr-1";

    private readonly IPluginInstallationService _installationService = Substitute.For<IPluginInstallationService>();
    private readonly IPluginConnectionService _connectionService = Substitute.For<IPluginConnectionService>();

    [Fact]
    public async Task TheProviderCallbackSendsAFinishedConsentThroughTheConnectPage()
    {
        // Not straight to the plugins page: the consent may have been given somewhere the app that
        // asked for it cannot see, and that page is the only thing that can hand the user back.
        ConfigureState(GoogleDriveKey);
        _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns(Connected(GoogleDriveKey));

        var result = await CreateSut().GoogleOAuthCallback("oauth-code", "state-token", null, CancellationToken.None);

        var (path, query) = Redirect(result);
        Assert.Equal(GoogleConnectPage, path);
        Assert.Equal(PluginConstants.CallbackStatus.Connected, query["status"]);
        Assert.Equal(GoogleDriveKey, query["plugin"]);
        Assert.False(query.ContainsKey("reason"));
        // Nothing to diagnose, so nothing to quote.
        Assert.False(query.ContainsKey("ref"));
    }

    [Fact]
    public async Task AConsentGivenFromTheDesktopAppSaysSo()
    {
        // The callback page turns this into a warptalk:// link. It comes from the sealed state and
        // never from the request, which is what stops a crafted link opening someone's app.
        ConfigureState(GoogleDriveKey, PluginConstants.OAuthClient.Desktop);
        _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns(Connected(GoogleDriveKey, client: PluginConstants.OAuthClient.Desktop));

        var result = await CreateSut().GoogleOAuthCallback("oauth-code", "state-token", null, CancellationToken.None);

        var (path, query) = Redirect(result);
        Assert.Equal(GoogleConnectPage, path);
        Assert.Equal(PluginConstants.OAuthClient.Desktop, query["client"]);
    }

    [Fact]
    public async Task ANarrowedConsentIsReportedAsPartial_NotAsSuccessOrFailure()
    {
        // The user unticked a scope. The grant is real, so calling it an error would be wrong - and
        // calling it a success would leave them wondering why half the plugin does nothing.
        ConfigureState(GoogleDriveKey);
        _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns(Connected(GoogleDriveKey, status: PluginConstants.CallbackStatus.Partial));

        var result = await CreateSut().GoogleOAuthCallback("oauth-code", "state-token", null, CancellationToken.None);

        var (_, query) = Redirect(result);
        Assert.Equal(PluginConstants.CallbackStatus.Partial, query["status"]);
        Assert.False(query.ContainsKey("reason"));
    }

    [Fact]
    public async Task TheProviderCallbackReportsACancelledConsent_WithoutExchangingAnything()
    {
        // Google answers a Cancel with error=access_denied and no code at all. Exchanging in that
        // case posts an empty code and turns the user's own choice into a provider failure.
        ConfigureState(GoogleDriveKey);

        var result = await CreateSut()
            .GoogleOAuthCallback(null, "state-token", "access_denied", CancellationToken.None);

        var (path, query) = Redirect(result);
        // Straight to the plugins page: nothing was exchanged, so no plugin was looked up and there
        // is no provider whose connect page this could go through.
        Assert.Equal(PluginsPage, path);
        Assert.Equal(PluginConstants.CallbackStatus.Error, query["status"]);
        Assert.Equal(PluginConstants.ErrorCodes.AccessDenied, query["reason"]);
        Assert.Equal(GoogleDriveKey, query["plugin"]);
        await _connectionService.DidNotReceive().CompleteProviderOAuthCallbackAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheProviderCallbackReportsAProviderError_WhenGoogleRefusesForSomeOtherReason()
    {
        ConfigureState(GoogleDriveKey);

        var result = await CreateSut()
            .GoogleOAuthCallback(null, "state-token", "server_error", CancellationToken.None);

        var (_, query) = Redirect(result);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, query["reason"]);
    }

    [Fact]
    public async Task TheProviderCallbackOmitsThePlugin_WhenTheStateCannotBeRead()
    {
        // An unreadable state carries no plugin key, and naming the wrong tile would be worse than
        // naming none.
        _connectionService.ReadFlowHint(Arg.Any<string>()).Returns((PluginOAuthFlowHintDto?)null);

        var result = await CreateSut()
            .GoogleOAuthCallback("oauth-code", "tampered-state", null, CancellationToken.None);

        var (path, query) = Redirect(result);
        Assert.Equal(PluginsPage, path);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, query["reason"]);
        Assert.False(query.ContainsKey("plugin"));
        await _connectionService.DidNotReceive().CompleteProviderOAuthCallbackAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedConsentCarriesTheReferenceItsLogLineIsKeyedBy()
    {
        // The one thing a user can quote that turns "it did not work" into a line an operator can
        // find. Only on a failure: there is nothing to look up when it worked.
        ConfigureState(GoogleDriveKey);
        _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns(Refused(PluginConstants.ErrorCodes.ProviderConfiguration, GoogleDriveKey));

        var result = await CreateSut().GoogleOAuthCallback("oauth-code", "state-token", null, CancellationToken.None);

        var (_, query) = Redirect(result);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderConfiguration, query["reason"]);
        Assert.Equal(CorrelationId, query["ref"]);
    }

    [Fact]
    public async Task TheProviderCallbackRedirects_WhenCompletionThrows()
    {
        // The service reports its own failures rather than throwing, so this is the second line of
        // defence - and the one that decides whether anything unforeseen reaches a person who has
        // just consented as a raw API error page.
        ConfigureState(GoogleDriveKey);
        _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns<PluginOAuthCallbackOutcomeDto>(_ => throw new InvalidOperationException("the database went away"));

        var result = await CreateSut().GoogleOAuthCallback("oauth-code", "state-token", null, CancellationToken.None);

        var (path, query) = Redirect(result);
        Assert.Equal(PluginsPage, path);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, query["reason"]);
        Assert.Equal(GoogleDriveKey, query["plugin"]);
    }

    [Fact]
    public async Task TheLegacyCallbackNamesThePluginFromItsPath()
    {
        // The one callback whose path carries the key, so the tile is known even for a state that
        // will not unprotect - and the only key that can arrive is the retired one.
        _connectionService.ReadFlowHint(Arg.Any<string>()).Returns((PluginOAuthFlowHintDto?)null);
        _connectionService.CompleteOAuthCallbackAsync(
                RetiredWorkspaceKey, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns(Refused(PluginConstants.ErrorCodes.PermissionDenied, RetiredWorkspaceKey));

        var result = await CreateSut()
            .OAuthCallback(RetiredWorkspaceKey, "oauth-code", "state-token", null, CancellationToken.None);

        var (path, query) = Redirect(result);
        // Through the connect page rather than the plugins page: the service got far enough to
        // resolve the plugin, so the provider is known even though the flow failed.
        Assert.Equal(GoogleConnectPage, path);
        Assert.Equal(RetiredWorkspaceKey, query["plugin"]);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, query["reason"]);
    }

    [Fact]
    public async Task TheMcpCallbackCarriesTheSameOutcomeContract()
    {
        // One vocabulary for all three routes: the app reads the outcome the same way whichever
        // provider sent the user back.
        ConfigureState("linear");
        _connectionService.CompleteMcpOAuthCallbackAsync(
                "oauth-code", "state-token", "https://issuer.test", Arg.Any<CancellationToken>())
            .Returns(Connected("linear", provider: "linear"));

        var result = await CreateSut()
            .McpOAuthCallback("oauth-code", "state-token", null, "https://issuer.test", CancellationToken.None);

        var (path, query) = Redirect(result);
        Assert.Equal($"{AppBaseUrl}/connect/linear/callback", path);
        Assert.Equal(PluginConstants.CallbackStatus.Connected, query["status"]);
        Assert.Equal("linear", query["plugin"]);
    }

    private void ConfigureState(string pluginKey, string client = PluginConstants.OAuthClient.Web) =>
        _connectionService.ReadFlowHint("state-token").Returns(new PluginOAuthFlowHintDto(pluginKey, client));

    private static PluginOAuthCallbackOutcomeDto Connected(
        string pluginKey,
        string provider = PluginConstants.Providers.Google,
        string status = PluginConstants.CallbackStatus.Connected,
        string client = PluginConstants.OAuthClient.Web) =>
        new(
            status,
            null,
            provider,
            pluginKey,
            client,
            new PluginConnectionStatusDto(
                pluginKey,
                PluginConstants.ConnectionStatus.Connected,
                "user@example.com",
                ["https://www.googleapis.com/auth/drive.readonly"]));

    private static PluginOAuthCallbackOutcomeDto Refused(string reason, string pluginKey) =>
        new(
            PluginConstants.CallbackStatus.Error,
            reason,
            PluginConstants.Providers.Google,
            pluginKey,
            PluginConstants.OAuthClient.Web,
            null);

    private static (string Path, IDictionary<string, string> Query) Redirect(IActionResult result)
    {
        var url = Assert.IsType<RedirectResult>(result).Url;
        var separator = url.IndexOf('?');
        if (separator < 0) return (url, new Dictionary<string, string>());

        var query = QueryHelpers.ParseQuery(url[(separator + 1)..])
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
        return (url[..separator], query);
    }

    private AssistantPluginsController CreateSut()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["CorrelationId"] = CorrelationId;

        return new AssistantPluginsController(
            _installationService,
            _connectionService,
            NullLogger<AssistantPluginsController>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AppBaseUrl"] = AppBaseUrl })
                .Build())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
