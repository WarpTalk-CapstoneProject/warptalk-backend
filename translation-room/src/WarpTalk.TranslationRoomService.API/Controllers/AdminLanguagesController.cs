using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// The language catalog room validation actually reads, for the System Admin portal.
///
/// Unlike the public listing this includes INACTIVE languages: "Korean is present and switched off"
/// and "Korean is not in the catalog" produce the same rejection and need completely different
/// fixes.
///
/// WT-691: no longer read-only. It was, because this service has no bus and a platform-wide switch
/// that leaves no trace of who threw it is not one to put behind a button. Every write below now
/// records itself in the platform audit log over gRPC first (the transport auth already uses) and
/// is abandoned when that record fails. There is no DELETE: disabling is the soft switch, and a
/// room that already ran in a language keeps naming it.
/// </summary>
[ApiController]
[Route("api/v1/admin/languages")]
public class AdminLanguagesController : ControllerBase
{
    private readonly IAdminLanguageService _languages;

    public AdminLanguagesController(IAdminLanguageService languages)
    {
        _languages = languages;
    }

    /// <summary>Every row, inactive included, with live and scheduled meeting counts.</summary>
    [HttpGet]
    [RequirePermission(AdminPermissions.SettingsRead)]
    public async Task<IActionResult> Get(CancellationToken ct)
        => ToActionResult(await _languages.GetCatalogAsync(ct));

    [HttpPost]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> Create([FromBody] AdminCreateLanguageRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        var result = await _languages.CreateAsync(request, actor, ct);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : ToActionResult(result);
    }

    [HttpPut("{code}")]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> Update(string code, [FromBody] AdminUpdateLanguageRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _languages.UpdateAsync(code, request, actor, ct));
    }

    [HttpPost("{code}/enable")]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> Enable(string code, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _languages.EnableAsync(code, actor, ct));
    }

    /// <summary>
    /// Soft disable. 409 while a live meeting uses the language; 409 for scheduled ones unless the
    /// body confirms them.
    /// </summary>
    [HttpPost("{code}/disable")]
    [RequirePermission(AdminPermissions.SettingsManage)]
    public async Task<IActionResult> Disable(
        string code, [FromBody] AdminDisableLanguageRequest? request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(
            await _languages.DisableAsync(code, request ?? new AdminDisableLanguageRequest(), actor, ct));
    }

    /// <summary>The actor comes from the token, never from the request.</summary>
    private bool TryResolveActor(out AdminActorContext actor) =>
        AdminActorContext.TryResolve(User, HttpContext, out actor);

    private IActionResult UnauthorizedActor() =>
        Unauthorized(new ApiErrorResponse("The token carries no user id.", ErrorCodes.Unauthorized));

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Conflict or ErrorCodes.InvalidState => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
