using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
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
    private readonly string _appBaseUrl;

    public AssistantPluginsController(
        IPluginInstallationService installationService,
        IPluginConnectionService connectionService,
        IConfiguration configuration)
    {
        _installationService = installationService;
        _connectionService = connectionService;
        _appBaseUrl = (configuration["AppBaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PluginCatalogItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListCatalog(CancellationToken ct)
    {
        var result = await _installationService.ListCatalogAsync(CurrentUserId, ct);
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
    public async Task<IActionResult> Install(string pluginKey, CancellationToken ct)
    {
        var result = await _installationService.InstallAsync(pluginKey, CurrentUserId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == PluginConstants.ErrorCodes.UnknownPlugin) return NotFound(result.Error);
            return BadRequest(result.Error);
        }
        return Ok(result.Value);
    }

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
    public async Task<IActionResult> GetConnectUrl(string pluginKey, CancellationToken ct)
    {
        var result = await _connectionService.GetConnectUrlAsync(pluginKey, CurrentUserId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == PluginConstants.ErrorCodes.UnknownPlugin) return NotFound(result.Error);
            if (result.ErrorCode == PluginConstants.ErrorCodes.PluginNotInstalled) return Conflict(result.Error);
            return BadRequest(result.Error);
        }
        return Ok(result.Value);
    }

    /// <remarks>
    /// Google redirects the end user's browser straight at this gateway URL, so the response has
    /// to be a redirect back into the app rather than a JSON body: nothing renders raw API JSON
    /// for a human. The plugins page re-fetches connection status on load, so it reflects the
    /// outcome without any query-string contract between this endpoint and the frontend.
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
        var pluginsPageUrl = $"{_appBaseUrl}/settings/plugins";

        if (string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(state))
            await _connectionService.CompleteMcpOAuthCallbackAsync(code, state, iss, ct);

        return Redirect(pluginsPageUrl);
    }

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
        var pluginsPageUrl = $"{_appBaseUrl}/settings/plugins";

        if (string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(state))
            await _connectionService.CompleteOAuthCallbackAsync(pluginKey, code, state, ct);

        return Redirect(pluginsPageUrl);
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
