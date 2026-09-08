using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The OAuth callbacks are the only actions in the product whose caller is a human's browser rather
/// than the frontend, so what they return is a page the user lands on. These pin that every way a
/// consent can end - including the ways it ends badly - lands them back on the plugins page with
/// the outcome in the query string.
/// </summary>
/// <remarks>
/// Two failures motivated the whole set. The actions used to test <c>error</c> only for emptiness,
/// so a user pressing Cancel had their empty code sent to Google as if it were real. And nothing
/// caught an exception, so a 429 mid-redirect ended on a raw API error page - the exact thing the
/// actions' own doc comment says must never happen.
/// </remarks>
public class AssistantPluginsOAuthCallbackTests
{
    private const string AppBaseUrl = "https://app.warptalk.test";
    private const string PluginsPage = $"{AppBaseUrl}/settings/plugins";
    private const string GoogleDriveKey = "google_drive";
    private const string RetiredWorkspaceKey = "google_workspace";

    private readonly IPluginInstallationService _installationService = Substitute.For<IPluginInstallationService>();
    private readonly IPluginConnectionService _connectionService = Substitute.For<IPluginConnectionService>();

    [Fact]
    public async Task TheProviderCallbackReportsSuccess_WithThePluginItConnected()
    {
        // The page cannot tell a completed consent from a cancelled one by re-fetching status: a
        // cancelled consent leaves the status exactly as it was. So the outcome travels in the URL.
        ConfigureState(GoogleDriveKey);
        _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns(Connected(GoogleDriveKey));

        var result = await CreateSut().GoogleOAuthCallback("oauth-code", "state-token", null, CancellationToken.None);

        Assert.Equal($"{PluginsPage}?plugin={GoogleDriveKey}&connected=1", RedirectUrl(result));
    }

    [Fact]
    public async Task TheProviderCallbackReportsACancelledConsent_WithoutExchangingAnything()
    {
        // Google answers a Cancel with error=access_denied and no code at all. Exchanging in that
        // case posts an empty code and turns the user's own choice into a provider failure.
        ConfigureState(GoogleDriveKey);

        var result = await CreateSut()
            .GoogleOAuthCallback(null, "state-token", "access_denied", CancellationToken.None);

        Assert.Equal($"{PluginsPage}?plugin={GoogleDriveKey}&error=access_denied", RedirectUrl(result));
        await _connectionService.DidNotReceive().CompleteProviderOAuthCallbackAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheProviderCallbackReportsAProviderError_WhenGoogleRefusesForSomeOtherReason()
    {
        ConfigureState(GoogleDriveKey);

        var result = await CreateSut()
            .GoogleOAuthCallback(null, "state-token", "server_error", CancellationToken.None);

        Assert.Equal($"{PluginsPage}?plugin={GoogleDriveKey}&error=provider_error", RedirectUrl(result));
    }

    [Fact]
    public async Task TheProviderCallbackOmitsThePlugin_WhenTheStateCannotBeRead()
    {
        // An unreadable state carries no plugin key, and naming the wrong tile would be worse than
        // naming none.
        _connectionService.ReadPluginKeyFromState(Arg.Any<string>()).Returns((string?)null);

        var result = await CreateSut()
            .GoogleOAuthCallback("oauth-code", "tampered-state", null, CancellationToken.None);

        Assert.Equal($"{PluginsPage}?error=invalid_state", RedirectUrl(result));
        await _connectionService.DidNotReceive().CompleteProviderOAuthCallbackAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheProviderCallbackRedirects_WhenTheExchangeFails()
    {
        // A 429 or a 503 from Google arrives while the browser is mid-redirect. The user has to end
        // up somewhere they can press Connect again.
        ConfigureState(GoogleDriveKey);
        _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PluginConnectionStatusDto>(
                "The provider could not complete the connection. Try connecting again in a moment.",
                PluginConstants.ErrorCodes.ProviderUnavailable));

        var result = await CreateSut().GoogleOAuthCallback("oauth-code", "state-token", null, CancellationToken.None);

        Assert.Equal($"{PluginsPage}?plugin={GoogleDriveKey}&error=exchange_failed", RedirectUrl(result));
    }

    [Fact]
    public async Task TheProviderCallbackRedirects_WhenCompletionThrows()
    {
        // Nothing unforeseen may reach the user as an API error page either.
        ConfigureState(GoogleDriveKey);
        _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns<Result<PluginConnectionStatusDto>>(_ => throw new InvalidOperationException("the database went away"));

        var result = await CreateSut().GoogleOAuthCallback("oauth-code", "state-token", null, CancellationToken.None);

        Assert.Equal($"{PluginsPage}?plugin={GoogleDriveKey}&error=exchange_failed", RedirectUrl(result));
    }

    [Fact]
    public async Task TheProviderCallbackReportsAnUnknownPlugin()
    {
        ConfigureState(GoogleDriveKey);
        _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PluginConnectionStatusDto>(
                "Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin));

        var result = await CreateSut().GoogleOAuthCallback("oauth-code", "state-token", null, CancellationToken.None);

        Assert.Equal($"{PluginsPage}?plugin={GoogleDriveKey}&error=unknown_plugin", RedirectUrl(result));
    }

    [Fact]
    public async Task TheLegacyCallbackNamesThePluginFromItsPath()
    {
        // The one callback whose path carries the key, so the tile is known even for a state that
        // will not unprotect - and the only key that can arrive is the retired one.
        _connectionService.ReadPluginKeyFromState(Arg.Any<string>()).Returns((string?)null);
        _connectionService.CompleteOAuthCallbackAsync(
                RetiredWorkspaceKey, "oauth-code", "state-token", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PluginConnectionStatusDto>(
                "Invalid OAuth state.", PluginConstants.ErrorCodes.PermissionDenied));

        var result = await CreateSut()
            .OAuthCallback(RetiredWorkspaceKey, "oauth-code", "state-token", null, CancellationToken.None);

        Assert.Equal($"{PluginsPage}?plugin={RetiredWorkspaceKey}&error=invalid_state", RedirectUrl(result));
    }

    [Fact]
    public async Task TheMcpCallbackCarriesTheSameOutcomeContract()
    {
        // One vocabulary for all three routes: the plugins page reads its URL the same way whichever
        // provider sent the user back.
        ConfigureState("linear");
        _connectionService.CompleteMcpOAuthCallbackAsync(
                "oauth-code", "state-token", "https://issuer.test", Arg.Any<CancellationToken>())
            .Returns(Connected("linear"));

        var result = await CreateSut()
            .McpOAuthCallback("oauth-code", "state-token", null, "https://issuer.test", CancellationToken.None);

        Assert.Equal($"{PluginsPage}?plugin=linear&connected=1", RedirectUrl(result));
    }

    private void ConfigureState(string pluginKey) =>
        _connectionService.ReadPluginKeyFromState("state-token").Returns(pluginKey);

    private static Result<PluginConnectionStatusDto> Connected(string pluginKey) =>
        Result.Success(new PluginConnectionStatusDto(
            pluginKey,
            PluginConstants.ConnectionStatus.Connected,
            "user@example.com",
            ["https://www.googleapis.com/auth/drive.readonly"]));

    private static string RedirectUrl(IActionResult result) =>
        Assert.IsType<RedirectResult>(result).Url;

    private AssistantPluginsController CreateSut()
    {
        return new AssistantPluginsController(
            _installationService,
            _connectionService,
            NullLogger<AssistantPluginsController>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AppBaseUrl"] = AppBaseUrl })
                .Build());
    }
}
