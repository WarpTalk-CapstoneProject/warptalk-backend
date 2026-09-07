using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

/// <summary>
/// The operator's half of the plugin catalog: everything about a row's life after the INSERT.
/// </summary>
/// <remarks>
/// <c>POST api/v1/assistant/plugins/catalog</c> on <see cref="AssistantPluginsController"/> already
/// made "the catalog is data, not code" true for creating a row. It was also the only admin
/// endpoint, so every other change to a row meant SQL against a running database: a wrong OAuth
/// client id could not be rotated, and adding one tool to <c>tools_json</c> cost a migration and a
/// deploy - five migrations exist for nothing else.
/// <para>
/// Deliberately a separate controller from <see cref="AssistantPluginsController"/>. That one is
/// what a signed-in user's plugins page calls and is <c>[Authorize]</c> at the class level; this one
/// writes the global catalog every user reads and is gated on
/// <see cref="SystemAdminAuthorization.PolicyName"/> at the class level. Two audiences, two
/// attributes, and no per-action decision about which one applies - which is the mistake a single
/// mixed controller invites.
/// </para>
/// <para>
/// ROUTING. These endpoints sit under the same <c>api/v1/assistant/plugins</c> prefix as the
/// user-facing controller, which routes <c>{pluginKey}</c> at the same depth. ASP.NET compares
/// route segments left to right and a literal outranks a parameter, so at two segments
/// <c>catalog/{pluginKey}</c> here beats <c>{pluginKey}/connection</c> and
/// <c>{pluginKey}/connect-url</c> there. No request is ever ambiguous - but a plugin genuinely
/// keyed <c>catalog</c> would have its own user-facing calls answered by this controller, and a
/// normal user would get a 403 where their connection status belongs. <c>catalog</c> is therefore
/// reserved: see <see cref="PluginConstants.ReservedPluginKeys"/>, which also records that the
/// <c>plugins_plugin_key_not_reserved</c> database constraint still names only <c>mcp</c> and needs
/// a migration to catch up.
/// </para>
/// <para>
/// SECRETS. No response from this controller carries an OAuth client secret, in any form. A secret
/// enters through <c>PUT .../oauth</c>, is encrypted by <c>IPluginCredentialProtector</c> before it
/// is stored, and is never read back - <c>hasClientSecret</c> is the whole of what can be learned
/// about it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/assistant/plugins/catalog")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AssistantPluginCatalogController : ControllerBase
{
    private readonly IPluginCatalogAdminService _catalogAdminService;

    public AssistantPluginCatalogController(IPluginCatalogAdminService catalogAdminService)
    {
        _catalogAdminService = catalogAdminService;
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    /// <summary>
    /// Every catalog row, including the ones retired with <c>is_active = false</c>.
    /// </summary>
    /// <remarks>
    /// The user-facing listing filters those out, which leaves a retired row invisible in the one
    /// place someone would go to bring it back.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PluginCatalogAdminListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await _catalogAdminService.ListAsync(ct);
        return ToResponse(result);
    }

    /// <summary>One catalog row, with its tool manifest.</summary>
    [HttpGet("{pluginKey}")]
    [ProducesResponseType(typeof(PluginCatalogAdminDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string pluginKey, CancellationToken ct)
    {
        var result = await _catalogAdminService.GetAsync(pluginKey, ct);
        return ToResponse(result);
    }

    /// <summary>
    /// Edits a row. Only the properties present in the body change.
    /// </summary>
    /// <remarks>
    /// <c>avatarUrl</c> and <c>category</c> are cleared by sending an empty string; sending null is
    /// indistinguishable from omitting the property.
    /// </remarks>
    [HttpPatch("{pluginKey}")]
    [ProducesResponseType(typeof(PluginCatalogAdminDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        string pluginKey,
        [FromBody] UpdatePluginCatalogRequest request,
        CancellationToken ct)
    {
        var result = await _catalogAdminService.UpdateAsync(pluginKey, request, CurrentUserId, ct);
        return ToResponse(result);
    }

    /// <summary>
    /// Sets or rotates the row's pre-registered OAuth client and marks it
    /// <c>oauth_client_source = 'preregistered'</c>.
    /// </summary>
    /// <remarks>
    /// This is the endpoint that stops a wrong client id in production being a SQL job. The secret
    /// goes in and does not come back: omit <c>clientSecret</c> to rotate the id alone, send an
    /// empty string to clear the stored secret.
    /// </remarks>
    [HttpPut("{pluginKey}/oauth")]
    [ProducesResponseType(typeof(PluginCatalogAdminDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetOAuthClient(
        string pluginKey,
        [FromBody] SetPluginOAuthClientRequest request,
        CancellationToken ct)
    {
        var result = await _catalogAdminService.SetOAuthClientAsync(pluginKey, request, CurrentUserId, ct);
        return ToResponse(result);
    }

    /// <summary>
    /// Replaces the row's tool manifest wholesale, after validating it against the tool contract.
    /// </summary>
    /// <remarks>
    /// The validation is strict because a malformed manifest fails silently: WarpBot simply stops
    /// choosing the tool, days later and nowhere near this call. Every problem in the submitted
    /// manifest comes back at once.
    /// </remarks>
    [HttpPut("{pluginKey}/tools")]
    [ProducesResponseType(typeof(PluginCatalogAdminDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReplaceTools(
        string pluginKey,
        [FromBody] ReplacePluginToolsRequest request,
        CancellationToken ct)
    {
        var result = await _catalogAdminService.ReplaceToolsAsync(pluginKey, request, CurrentUserId, ct);
        return ToResponse(result);
    }

    /// <summary>
    /// Clears the cached OAuth discovery output so the registration ladder runs again on the next
    /// connect.
    /// </summary>
    /// <remarks>
    /// For a server that has moved its endpoints, or one whose well-known documents were wrong when
    /// we first read them. A pre-registered client id survives; a CIMD or DCR client does not, since
    /// it was issued by the very endpoints being cleared.
    /// </remarks>
    [HttpPost("{pluginKey}/rediscover")]
    [ProducesResponseType(typeof(PluginCatalogAdminDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rediscover(string pluginKey, CancellationToken ct)
    {
        var result = await _catalogAdminService.RediscoverAsync(pluginKey, CurrentUserId, ct);
        return ToResponse(result);
    }

    /// <summary>
    /// Retires a row. Soft by default; <c>?hard=true</c> deletes it outright, and only while
    /// nothing references it.
    /// </summary>
    /// <remarks>
    /// <c>plugin_connections_plugin_id_fkey</c> has been <c>ON DELETE RESTRICT</c> since
    /// 20260907101000, so a hard delete against a referenced row would come back as a database
    /// exception. It is refused here instead, with the counts that explain the refusal.
    /// </remarks>
    [HttpDelete("{pluginKey}")]
    [ProducesResponseType(typeof(PluginCatalogDeleteResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(
        string pluginKey,
        [FromQuery] bool hard,
        CancellationToken ct)
    {
        var result = await _catalogAdminService.DeleteAsync(pluginKey, hard, CurrentUserId, ct);
        return ToResponse(result);
    }

    /// <summary>
    /// A page of this plugin's recorded tool invocations, newest first.
    /// </summary>
    /// <remarks>
    /// <c>plugin_tool_audits</c> has been written on every tool call since 20260823090000 and read
    /// by nothing. This is the first endpoint that reads it, which is what turns it from a table
    /// that accumulates into a record anyone can actually consult.
    /// </remarks>
    /// <param name="pluginKey">The catalog row to read audits for.</param>
    /// <param name="userId">Optional: only calls made by this user.</param>
    /// <param name="outcome">
    /// Optional: only calls whose <c>result_status</c> matches - <c>ok</c>, or an error code such as
    /// <c>missing_scope</c>.
    /// </param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page; clamped to 1-200.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{pluginKey}/audits")]
    [ProducesResponseType(typeof(PluginToolAuditPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAudits(
        string pluginKey,
        [FromQuery] Guid? userId,
        [FromQuery] string? outcome,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken ct)
    {
        // Passed through unnormalised on purpose: the service owns what an out-of-range page means,
        // so a missing query parameter arriving here as 0 and an explicit 0 get the same answer.
        var query = new PluginToolAuditQueryDto(userId, outcome, page, pageSize);

        var result = await _catalogAdminService.ListAuditsAsync(pluginKey, query, ct);
        return ToResponse(result);
    }

    /// <summary>
    /// Maps a service result onto a status code by its error code, so every endpoint answers the
    /// same failure the same way.
    /// </summary>
    private IActionResult ToResponse<T>(WarpTalk.Shared.Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        var body = new { error = result.Error, errorCode = result.ErrorCode };
        return result.ErrorCode switch
        {
            PluginConstants.ErrorCodes.UnknownPlugin => NotFound(body),
            PluginConstants.ErrorCodes.PluginInUse => Conflict(body),
            _ => BadRequest(body),
        };
    }
}
