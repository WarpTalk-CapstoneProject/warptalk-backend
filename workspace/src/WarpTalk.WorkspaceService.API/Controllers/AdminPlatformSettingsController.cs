using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Services;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// The platform settings console (/admin/settings): the typed registry
/// (WarpTalk.Shared.PlatformSettings.PlatformSettingsCatalog) with the values operators stored, their
/// history, and the writes. Every write is validated against the registry, recorded in the setting's
/// history and in the admin audit log (entity type platform_setting) in the same save, and published
/// to Redis for the services that read it.
///
/// A Security &amp; auth setting needs settings.security on top of settings.manage; the service checks
/// that per key, since an import or a revert can reach any key.
///
/// Setting keys are dotted (<c>security.session.access_token_minutes</c>) and travel in the path.
/// </summary>
[ApiController]
[Route("api/v1/admin/settings")]
public sealed class AdminPlatformSettingsController : ControllerBase
{
    private readonly IPlatformSettingsAdminService _settings;
    private readonly IPlatformIntegrationsService _integrations;

    public AdminPlatformSettingsController(IPlatformSettingsAdminService settings, IPlatformIntegrationsService integrations)
    {
        _settings = settings;
        _integrations = integrations;
    }

    [HttpGet]
    [RequirePermission(AdminPermissions.SettingsRead)]
    public async Task<IActionResult> Get(CancellationToken ct)
        => ToActionResult(await _settings.GetAsync(User, ct));

    /// <summary>Every setting's changes, newest first; <c>?key=</c> narrows to one.</summary>
    [HttpGet("history")]
    [RequirePermission(AdminPermissions.SettingsRead)]
    public async Task<IActionResult> GetHistory([FromQuery] string? key, [FromQuery] int limit = 50, CancellationToken ct = default)
        => ToActionResult(await _settings.GetHistoryAsync(string.IsNullOrWhiteSpace(key) ? null : key, limit, ct));

    [HttpPut("{key}")]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> Set(string key, [FromBody] PlatformSettingWriteRequest request, CancellationToken ct)
        => ToActionResult(await _settings.SetAsync(User, key, request, Correlation(), ct));

    /// <summary>Removes the stored value at one scope, so the owning service falls back to its own configuration.</summary>
    [HttpPost("{key}/reset")]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> Reset(string key, [FromBody] PlatformSettingResetRequest? request, CancellationToken ct)
        => ToActionResult(await _settings.ResetAsync(User, key, request ?? new PlatformSettingResetRequest(null, null, null, null), Correlation(), ct));

    /// <summary>Restores the value a change replaced. The revert is itself a new change.</summary>
    [HttpPost("history/{changeId:guid}/revert")]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> Revert(Guid changeId, [FromBody] PlatformSettingRevertRequest? request, CancellationToken ct)
        => ToActionResult(await _settings.RevertAsync(User, changeId, request ?? new PlatformSettingRevertRequest(null), Correlation(), ct));

    /// <summary>Every stored value except the sensitive ones, as a file the import accepts. Audited.</summary>
    [HttpPost("export")]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> Export(CancellationToken ct)
        => ToActionResult(await _settings.ExportAsync(User, Correlation(), ct));

    /// <summary><c>dryRun: true</c> returns the plan; otherwise all-or-nothing, with a required reason.</summary>
    [HttpPost("import")]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> Import([FromBody] PlatformSettingsImportRequest request, CancellationToken ct)
        => ToActionResult(await _settings.ImportAsync(User, request, Correlation(), ct));

    /// <summary>
    /// Every integration: configured yes/no per reporting service (never a value), the last
    /// connection check, and the read-only deploy configuration services report.
    /// </summary>
    [HttpGet("integrations")]
    [RequirePermission(AdminPermissions.SettingsRead)]
    public async Task<IActionResult> GetIntegrations(CancellationToken ct)
        => Ok(await _integrations.GetAsync(ct));

    /// <summary>A harmless read against one integration this service can reach (PING, readiness, a listing).</summary>
    [HttpPost("integrations/{key}/test")]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> TestIntegration(string key, CancellationToken ct)
        => ToActionResult(await _integrations.TestAsync(key, ct));

    private string? Correlation()
    {
        var value = HttpContext.Request.Headers[AdminActorContext.CorrelationHeader].ToString();
        if (string.IsNullOrWhiteSpace(value)) value = HttpContext.TraceIdentifier;
        return value.Length > 100 ? value[..100] : value;
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        var error = new ApiErrorResponse(result.Error, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.Unauthorized => Unauthorized(error),
            ErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, error),
            ErrorCodes.NotFound => NotFound(error),
            ErrorCodes.ValidationError => BadRequest(error),
            ErrorCodes.Conflict => Conflict(error),
            _ => StatusCode(StatusCodes.Status500InternalServerError, error),
        };
    }
}
