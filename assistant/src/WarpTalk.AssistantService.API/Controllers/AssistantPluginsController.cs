using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

[ApiController]
[Route("api/v1/assistant/plugins")]
[Authorize]
public class AssistantPluginsController : ControllerBase
{
    private readonly IPluginInstallationService _installationService;
    private readonly IPluginConnectionService _connectionService;
    private readonly ILogger<AssistantPluginsController> _logger;
    private readonly string _appBaseUrl;

    public AssistantPluginsController(
        IPluginInstallationService installationService,
        IPluginConnectionService connectionService,
        ILogger<AssistantPluginsController> logger,
        IConfiguration configuration)
    {
        _installationService = installationService;
        _connectionService = connectionService;
        _logger = logger;
        _appBaseUrl = (configuration["AppBaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    /// <param name="workspaceId">
    /// Optional. The workspace the user is browsing from. WT-646: supplying it makes each row
    /// carry that workspace's verdict in <c>workspacePolicyBlockReason</c>; omitting it lists the
    /// catalog with no workspace policy applied, which is what the personal plugins page does.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PluginCatalogItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListCatalog([FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        var result = await _installationService.ListCatalogAsync(CurrentUserId, workspaceId, ct);
        if (!result.IsSuccess) return BadRequest(result.Error);
        return Ok(result.Value);
    }

    /// <summary>
    /// Adds an MCP-backed app to the catalog.
    /// </summary>
    /// <remarks>
    /// This is what makes "the catalog is data, not code" real: the row appears in every user's
    /// plugin list immediately, with no deploy and no restart. Discovery and the client-registration
    /// ladder run on the first connect, so most servers need nothing beyond a key, a label and a URL.
    /// <para>
    /// Operator-scoped. It writes to a global catalog rather than to anything personal, so it is
    /// deliberately not reachable by an ordinary signed-in user the way install and connect are.
    /// </para>
    /// </remarks>
    [HttpPost("catalog")]
    [Authorize(Policy = SystemAdminAuthorization.PolicyName)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateMcpPlugin(
        [FromBody] CreateMcpPluginRequest request,
        CancellationToken ct)
    {
        var result = await _installationService.CreateMcpPluginAsync(request, CurrentUserId, ct);
        if (!result.IsSuccess)
            return BadRequest(new { error = result.Error, errorCode = result.ErrorCode });

        return CreatedAtAction(nameof(ListCatalog), new { }, result.Value);
    }

    [HttpPost("{pluginKey}/install")]
    [ProducesResponseType(typeof(PluginCatalogItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Install(string pluginKey, [FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        var result = await _installationService.InstallAsync(pluginKey, CurrentUserId, workspaceId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == PluginConstants.ErrorCodes.UnknownPlugin) return NotFound(result.Error);
            // 403, not 400: the request is well formed and the plugin exists - the workspace's
            // policy is what refuses it, and the client should say so rather than blame the input.
            if (IsWorkspacePolicyRefusal(result.ErrorCode)) return StatusCode(StatusCodes.Status403Forbidden, result.Error);
            return BadRequest(result.Error);
        }
        return Ok(result.Value);
    }

    private static bool IsWorkspacePolicyRefusal(string? errorCode) =>
        errorCode is PluginConstants.ErrorCodes.PermissionDenied;

    [HttpDelete("{pluginKey}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Disable(string pluginKey, CancellationToken ct)
    {
        var result = await _installationService.DisableAsync(pluginKey, CurrentUserId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == PluginConstants.ErrorCodes.UnknownPlugin) return NotFound(result.Error);
            if (result.ErrorCode == PluginConstants.ErrorCodes.PluginNotInstalled) return Conflict(result.Error);
            return BadRequest(result.Error);
        }
        return Ok();
    }

    [HttpGet("{pluginKey}/connection")]
    [ProducesResponseType(typeof(PluginConnectionStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConnectionStatus(string pluginKey, CancellationToken ct)
    {
        var result = await _connectionService.GetStatusAsync(pluginKey, CurrentUserId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == PluginConstants.ErrorCodes.UnknownPlugin) return NotFound(result.Error);
            return BadRequest(result.Error);
        }
        return Ok(result.Value);
    }

    [HttpGet("{pluginKey}/connect-url")]
    [ProducesResponseType(typeof(PluginConnectUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetConnectUrl(string pluginKey, [FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        var result = await _connectionService.GetConnectUrlAsync(pluginKey, CurrentUserId, workspaceId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == PluginConstants.ErrorCodes.UnknownPlugin) return NotFound(result.Error);
            if (result.ErrorCode == PluginConstants.ErrorCodes.PluginNotInstalled) return Conflict(result.Error);
            if (IsWorkspacePolicyRefusal(result.ErrorCode)) return StatusCode(StatusCodes.Status403Forbidden, result.Error);
            return BadRequest(result.Error);
        }
        return Ok(result.Value);
    }

    /// <remarks>
    /// The provider redirects the end user's browser straight at this gateway URL, so the response
    /// has to be a redirect back into the app rather than a JSON body: nothing renders raw API JSON
    /// for a human. That holds for the unhappy paths too, which is what
    /// <see cref="CallbackRedirect"/> is for - a 429 from the provider, a cancelled consent and an
    /// expired state all have to end as a page, never as an exception escaping the action.
    /// </remarks>
    /// <remarks>
    /// Every <c>kind='mcp'</c> plugin shares this one redirect URI. A Client ID Metadata Document
    /// has to enumerate its redirect URIs and the authorization server matches them exactly, so a
    /// per-plugin path would mean re-publishing that document - which servers cache for up to a
    /// week - every time a catalog row is added. That would defeat the whole point of adding an MCP
    /// app being an insert rather than a deploy.
    /// <para>
    /// The literal <c>mcp</c> segment wins over the <c>{pluginKey}</c> route below by ASP.NET's
    /// precedence rules, so a plugin may not be keyed <c>mcp</c>.
    /// </para>
    /// </remarks>
    [HttpGet("mcp/oauth/callback")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> McpOAuthCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? iss,
        CancellationToken ct)
    {
        var pluginKey = _connectionService.ReadPluginKeyFromState(state);

        var refusal = ClassifyBeforeExchange(pluginKey, code, state, error);
        if (refusal != null) return CallbackRedirect(pluginKey, refusal);

        return await CompleteCallbackAsync(
            pluginKey,
            () => _connectionService.CompleteMcpOAuthCallbackAsync(code!, state!, iss, ct));
    }

    /// <summary>
    /// The provider-scoped redirect URI for Google, shared by google_drive, google_calendar and
    /// google_meet.
    /// </summary>
    /// <remarks>
    /// Splitting google_workspace into three catalog rows would otherwise mean three redirect URIs
    /// registered in Google Cloud Console, and a console change - by hand, in a place no deploy
    /// touches - every time a Google product is added. One URI per provider is registered once.
    /// <para>
    /// The plugin key rides inside the protected <c>state</c>, exactly as it does for the MCP
    /// callback above; <c>state</c> was already carrying it, so nothing about the CSRF defence
    /// changes. The <c>google</c> segment is checked against the provider of the plugin the state
    /// names, so a state minted for another provider cannot be redeemed here.
    /// </para>
    /// <para>
    /// Both segments are literal, which is what keeps this clear of the <c>{pluginKey}</c> routes
    /// beside it. <c>{pluginKey}/oauth/callback</c> needs <c>oauth</c> in the middle segment and
    /// this has <c>google</c> there, so the two can never match the same path and - unlike the
    /// <c>mcp</c> callback, which forced the <c>plugins_plugin_key_not_reserved</c> constraint -
    /// this adds no newly reserved plugin key. A future <c>oauth/{provider}/callback</c> written
    /// with a route parameter instead would shadow a plugin keyed <c>oauth</c> and would need that
    /// constraint extended first.
    /// </para>
    /// </remarks>
    [HttpGet("oauth/google/callback")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> GoogleOAuthCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        // No plugin key in the path, so the tile to send the user back to can only come from the
        // state - which the provider returns even when it returns an error instead of a code.
        var pluginKey = _connectionService.ReadPluginKeyFromState(state);

        var refusal = ClassifyBeforeExchange(pluginKey, code, state, error);
        if (refusal != null) return CallbackRedirect(pluginKey, refusal);

        return await CompleteCallbackAsync(
            pluginKey,
            () => _connectionService.CompleteProviderOAuthCallbackAsync(
                PluginConstants.Providers.Google, code!, state!, ct));
    }

    /// <remarks>
    /// Superseded by the provider-scoped callbacks above and kept for one narrow case: a consent
    /// that was already at Google's consent screen when the provider-scoped redirect URI shipped
    /// comes back here, because the authorization request that started it named this path.
    /// Removing this route would strand exactly those.
    /// <para>
    /// OPERATOR: that only works while the old per-plugin URI is still an authorized redirect URI
    /// on the Google OAuth client alongside the new provider-scoped one - the token exchange has to
    /// repeat the URI the flow started with, so both have to be registered at once. An
    /// authorization code lives minutes, so once a deploy has settled nothing can still arrive
    /// here: at that point delete this route, the console entry, and
    /// <c>Plugins:GoogleWorkspace:OAuth:LegacyRedirectUri</c> together.
    /// </para>
    /// </remarks>
    [HttpGet("{pluginKey}/oauth/callback")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> OAuthCallback(
        string pluginKey,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        // The one callback whose path names the plugin, so the tile is known even for a state that
        // will not unprotect.
        var refusal = ClassifyBeforeExchange(pluginKey, code, state, error);
        if (refusal != null) return CallbackRedirect(pluginKey, refusal);

        return await CompleteCallbackAsync(
            pluginKey,
            () => _connectionService.CompleteOAuthCallbackAsync(pluginKey, code!, state!, ct));
    }

    // ---- the callback outcome contract -------------------------------------------------------

    /// <summary>
    /// The query-string vocabulary the plugins page reads off its own URL. Short, stable slugs: the
    /// page turns them into sentences, so nothing here is prose and nothing here is a provider's
    /// own error string.
    /// </summary>
    private static class CallbackError
    {
        /// <summary>The user pressed Cancel on the consent screen. Not a fault - do not alarm them.</summary>
        public const string AccessDenied = "access_denied";

        /// <summary>The state was missing, expired, tampered with, or names a different plugin than the path.</summary>
        public const string InvalidState = "invalid_state";

        /// <summary>The state named a plugin the catalog no longer serves.</summary>
        public const string UnknownPlugin = "unknown_plugin";

        /// <summary>The provider returned an error instead of a code, and it was not a cancellation.</summary>
        public const string ProviderError = "provider_error";

        /// <summary>Everything after the redirect: the token exchange, or storing the grant, failed.</summary>
        public const string ExchangeFailed = "exchange_failed";
    }

    /// <summary>
    /// The refusals that can be decided from the query string alone, before anything is exchanged.
    /// </summary>
    /// <remarks>
    /// The <c>error</c> parameter is the one every version of these actions used to only test for
    /// emptiness. Google sends <c>error=access_denied</c> with no code when the user cancels, so
    /// trying to exchange in that case would post an empty code and turn a deliberate choice into a
    /// provider failure.
    /// </remarks>
    private static string? ClassifyBeforeExchange(string? pluginKey, string? code, string? state, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return string.Equals(error, "access_denied", StringComparison.Ordinal)
                ? CallbackError.AccessDenied
                : CallbackError.ProviderError;
        }

        if (string.IsNullOrWhiteSpace(state) || pluginKey == null) return CallbackError.InvalidState;

        // A response with neither an error nor a code is not something any provider should send;
        // there is nothing to exchange either way.
        return string.IsNullOrWhiteSpace(code) ? CallbackError.ProviderError : null;
    }

    /// <summary>
    /// Runs the completion and turns whatever comes back - including a thrown exception - into a
    /// redirect.
    /// </summary>
    /// <remarks>
    /// The catch-all is deliberate and is the point of the whole helper: the caller is a browser
    /// mid-redirect, so an unhandled exception here is a raw API error page shown to a person who
    /// has just consented. Anything unexpected is reported as <c>exchange_failed</c>, which is the
    /// truthful summary from the user's side - the connection did not complete and connecting again
    /// is the thing to try.
    /// </remarks>
    private async Task<IActionResult> CompleteCallbackAsync(
        string? pluginKey,
        Func<Task<Result<PluginConnectionStatusDto>>> complete)
    {
        try
        {
            var result = await complete();
            return result.IsSuccess
                ? CallbackRedirect(result.Value!.PluginKey, errorCode: null)
                : CallbackRedirect(pluginKey, CallbackErrorFor(result.ErrorCode));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(
                e,
                "Completing the OAuth callback for plugin {PluginKey} threw; the user is being redirected "
                    + "back to the plugins page.",
                pluginKey);
            return CallbackRedirect(pluginKey, CallbackError.ExchangeFailed);
        }
    }

    /// <summary>
    /// Maps a domain error code onto the redirect vocabulary.
    /// </summary>
    /// <remarks>
    /// The default is not laziness: the set of slugs is a contract with the plugins page, so a
    /// domain code that gains no slug of its own has to collapse into the generic one rather than
    /// invent a sixth value the page has never heard of. From the user's side "the connection did
    /// not complete" is also all that separates the remaining cases.
    /// </remarks>
    private static string CallbackErrorFor(string? errorCode) => errorCode switch
    {
        PluginConstants.ErrorCodes.UnknownPlugin => CallbackError.UnknownPlugin,
        PluginConstants.ErrorCodes.PermissionDenied => CallbackError.InvalidState,
        _ => CallbackError.ExchangeFailed,
    };

    /// <summary>
    /// The one place a callback ends: back on the plugins page, saying what happened.
    /// </summary>
    /// <remarks>
    /// The page needs to tell a completed consent from a cancelled one, and re-fetching connection
    /// status cannot: a cancelled consent and a failed exchange both leave the status exactly as it
    /// was, so the page would show the tile unchanged with nothing said. Hence the outcome travels
    /// in the query string.
    /// <para>
    /// The plugin key is omitted when it could not be recovered - an unreadable state carries no
    /// key, and inventing one would point the page at the wrong tile.
    /// </para>
    /// </remarks>
    private IActionResult CallbackRedirect(string? pluginKey, string? errorCode)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(pluginKey)) query["plugin"] = pluginKey;
        if (errorCode == null) query["connected"] = "1";
        else query["error"] = errorCode;

        return Redirect($"{_appBaseUrl}/settings/plugins?{query}");
    }

    [HttpDelete("{pluginKey}/connection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disconnect(string pluginKey, CancellationToken ct)
    {
        var result = await _connectionService.DisconnectAsync(pluginKey, CurrentUserId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == PluginConstants.ErrorCodes.UnknownPlugin) return NotFound(result.Error);
            return BadRequest(result.Error);
        }
        return Ok();
    }
}
