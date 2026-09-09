using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
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

    /// <remarks>
    /// <c>client=desktop</c> says the caller is the Electron shell rather than a browser tab. It is
    /// sealed into the OAuth state here and read back at the callback, because by then the consent
    /// has happened in the system browser and nothing else on that request says where it started.
    /// </remarks>
    [HttpGet("{pluginKey}/connect-url")]
    [ProducesResponseType(typeof(PluginConnectUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetConnectUrl(
        string pluginKey,
        [FromQuery] string? client,
        [FromQuery] Guid? workspaceId,
        CancellationToken ct)
    {
        var result = await _connectionService.GetConnectUrlAsync(pluginKey, CurrentUserId, client, workspaceId, ct);
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
    /// for a human. That holds for the unhappy paths too, which is what <see cref="LandingUrl"/>
    /// is for - a 429 from the provider, a cancelled consent and an expired state all have to end
    /// as a page, never as an exception escaping the action. The app still re-fetches connection
    /// status on arrival, so the query string decides only which sentence to show - never what the
    /// connection actually is.
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
        var hint = _connectionService.ReadFlowHint(state);

        var refusal = ClassifyBeforeExchange(hint?.PluginKey, code, state, error);
        if (refusal != null) return Redirect(RefusedUrl(hint, refusal));

        return await CompleteAsync(
            hint,
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
        // No plugin key in the path, so the tile to send the user back to - and the surface to
        // send them back on - can only come from the state, which the provider returns even when
        // it returns an error instead of a code.
        var hint = _connectionService.ReadFlowHint(state);

        var refusal = ClassifyBeforeExchange(hint?.PluginKey, code, state, error);
        if (refusal != null) return Redirect(RefusedUrl(hint, refusal));

        return await CompleteAsync(
            hint,
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
        // will not unprotect. The surface still is not: that only ever lived in the state.
        var hint = _connectionService.ReadFlowHint(state)
            ?? new PluginOAuthFlowHintDto(pluginKey, PluginConstants.OAuthClient.Web);

        var refusal = ClassifyBeforeExchange(pluginKey, code, state, error);
        if (refusal != null) return Redirect(RefusedUrl(hint, refusal));

        return await CompleteAsync(
            hint,
            () => _connectionService.CompleteOAuthCallbackAsync(pluginKey, code!, state!, ct));
    }

    // ---- the callback outcome contract -------------------------------------------------------

    /// <summary>
    /// The refusals that can be decided from the query string alone, before anything is exchanged.
    /// </summary>
    /// <remarks>
    /// The <c>error</c> parameter is the one every version of these actions used to only test for
    /// emptiness. Google sends <c>error=access_denied</c> with no code when the user cancels, so
    /// trying to exchange in that case would post an empty code and turn a deliberate choice into a
    /// provider failure - and then tell the user something went wrong when nothing did.
    /// </remarks>
    private static string? ClassifyBeforeExchange(string? pluginKey, string? code, string? state, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return string.Equals(error, "access_denied", StringComparison.Ordinal)
                ? PluginConstants.ErrorCodes.AccessDenied
                : PluginConstants.ErrorCodes.ProviderUnavailable;
        }

        if (string.IsNullOrWhiteSpace(state) || pluginKey == null)
            return PluginConstants.ErrorCodes.PermissionDenied;

        // A response with neither an error nor a code is not something any provider should send;
        // there is nothing to exchange either way.
        return string.IsNullOrWhiteSpace(code) ? PluginConstants.ErrorCodes.ProviderUnavailable : null;
    }

    /// <summary>
    /// Runs the completion and turns whatever comes back - including a thrown exception - into a
    /// redirect.
    /// </summary>
    /// <remarks>
    /// The service already reports its own failures as an outcome rather than throwing, so the
    /// catch-all here is the second line rather than the first: the caller is a browser
    /// mid-redirect, and an exception escaping this action is a raw API error page shown to a
    /// person who has just consented. It costs four lines to make that impossible.
    /// </remarks>
    private async Task<IActionResult> CompleteAsync(
        PluginOAuthFlowHintDto? hint,
        Func<Task<PluginOAuthCallbackOutcomeDto>> complete)
    {
        try
        {
            return Redirect(LandingUrl(await complete()));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(
                e,
                "Completing the OAuth callback for plugin {PluginKey} threw; the user is being redirected "
                    + "back to the plugins page.",
                hint?.PluginKey);
            return Redirect(RefusedUrl(hint, PluginConstants.ErrorCodes.ProviderUnavailable));
        }
    }

    /// <summary>
    /// Where a finished callback sends the browser.
    /// </summary>
    /// <remarks>
    /// Through <c>/connect/{provider}/callback</c> rather than straight to the plugins page,
    /// because the consent may have been given somewhere the app that asked for it cannot see -
    /// the system browser for the desktop app, a second tab for the web one. That page is the only
    /// place that can hand the user back across that gap, and for the plain case it simply forwards
    /// to the plugins page with the same query.
    /// <para>
    /// The provider comes from the outcome and never from the request, so a state that did not
    /// survive lands on the plugins page instead of on a path an attacker could choose.
    /// </para>
    /// </remarks>
    private string LandingUrl(PluginOAuthCallbackOutcomeDto outcome)
    {
        var query = new Dictionary<string, string?> { ["status"] = outcome.Status };
        if (!string.IsNullOrWhiteSpace(outcome.PluginKey)) query["plugin"] = outcome.PluginKey;
        if (!string.IsNullOrWhiteSpace(outcome.Reason)) query["reason"] = outcome.Reason;
        if (outcome.Client == PluginConstants.OAuthClient.Desktop) query["client"] = outcome.Client;

        // Only on a failure, and only the id the logs are already keyed by: it is the one thing a
        // user can quote that turns "it did not work" into a line an operator can find.
        if (outcome.Status == PluginConstants.CallbackStatus.Error && CorrelationId is { Length: > 0 } reference)
            query["ref"] = reference;

        var path = string.IsNullOrWhiteSpace(outcome.Provider)
            ? $"{_appBaseUrl}/settings/plugins"
            : $"{_appBaseUrl}/connect/{Uri.EscapeDataString(outcome.Provider)}/callback";

        return QueryHelpers.AddQueryString(path, query);
    }

    /// <summary>
    /// Where a callback that never reached an exchange sends the browser.
    /// </summary>
    /// <remarks>
    /// Straight to the plugins page, not through the callback page: nothing was exchanged, so no
    /// plugin was ever looked up and the provider is unknown. A desktop user therefore lands in
    /// their browser rather than back in the app - the right trade for the one path where the user
    /// has already decided not to connect, and the reason <c>client</c> is still passed on so the
    /// page can say so.
    /// <para>
    /// The plugin key is omitted when it could not be recovered: an unreadable state carries no
    /// key, and inventing one would point the page at the wrong tile.
    /// </para>
    /// </remarks>
    private string RefusedUrl(PluginOAuthFlowHintDto? hint, string reason)
    {
        var query = new Dictionary<string, string?>
        {
            ["status"] = PluginConstants.CallbackStatus.Error,
            ["reason"] = reason,
        };
        if (CorrelationId is { Length: > 0 } reference) query["ref"] = reference;
        if (!string.IsNullOrWhiteSpace(hint?.PluginKey)) query["plugin"] = hint.PluginKey;
        if (hint?.Client == PluginConstants.OAuthClient.Desktop) query["client"] = hint.Client;

        return QueryHelpers.AddQueryString($"{_appBaseUrl}/settings/plugins", query);
    }

    /// <summary>The id this request's logs are already tagged with (see the middleware in Program).</summary>
    private string CorrelationId =>
        HttpContext.Items["CorrelationId"]?.ToString() is { Length: > 0 } id ? id : HttpContext.TraceIdentifier;

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
