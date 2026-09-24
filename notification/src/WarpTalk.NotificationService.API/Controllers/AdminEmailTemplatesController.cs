using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>
/// The email template CMS. Under /admin/notifications so it rides the gateway route the portal
/// already has for this service (AdminRouteExposureTests) instead of widening the admin surface.
/// </summary>
[ApiController]
[Route("api/v1/admin/notifications/email-templates")]
[RequirePermission(AdminPermissions.ContentEmailTemplates)]
public sealed class AdminEmailTemplatesController : ControllerBase
{
    private readonly IAdminEmailTemplateService _templates;

    public AdminEmailTemplatesController(IAdminEmailTemplateService templates)
    {
        _templates = templates;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await _templates.ListAsync(ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
    {
        var result = await _templates.GetAsync(key, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Save(string key, [FromBody] SaveEmailTemplateRequest request, CancellationToken ct)
    {
        if (AdminId() is not { } adminId) return Unauthorized();
        var result = await _templates.SaveAsync(adminId, key, request, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    /// <summary>Back to the built-in wording. Recorded in the history like any other change.</summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> Reset(string key, CancellationToken ct)
    {
        if (AdminId() is not { } adminId) return Unauthorized();
        var result = await _templates.ResetAsync(adminId, key, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpGet("{key}/versions")]
    public async Task<IActionResult> Versions(string key, CancellationToken ct)
    {
        var result = await _templates.ListVersionsAsync(key, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpPost("{key}/versions/{version:int}/restore")]
    public async Task<IActionResult> Restore(string key, int version, CancellationToken ct)
    {
        if (AdminId() is not { } adminId) return Unauthorized();
        var result = await _templates.RestoreAsync(adminId, key, version, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    /// <summary>Renders an unsaved edit with sample values, and lists what is wrong with it.</summary>
    [HttpPost("{key}/preview")]
    public IActionResult Preview(string key, [FromBody] EmailTemplateDraftRequest draft)
    {
        var result = _templates.Preview(key, draft);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    /// <summary>Sends an unsaved edit, filled with sample values, to the signed-in admin only.</summary>
    [HttpPost("{key}/test")]
    public async Task<IActionResult> SendTest(string key, [FromBody] EmailTemplateDraftRequest draft, CancellationToken ct)
    {
        if (AdminId() is null) return Unauthorized();
        var result = await _templates.SendTestAsync(key, draft, User.GetEmail(), ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    private Guid? AdminId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
