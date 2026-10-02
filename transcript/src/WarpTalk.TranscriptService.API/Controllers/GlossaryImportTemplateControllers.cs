using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using WarpTalk.TranscriptService.Application.GlossaryImportTemplates;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.API.Controllers;

/// <summary>
/// WT-880: the platform admin configures the glossary import FILE SHAPE — the "Import template"
/// tab of /admin/global-glossary. One configuration for the whole platform; no term content.
/// </summary>
[ApiController]
[Route("api/v1/admin/global-glossary/import-template")]
public class GlobalGlossaryImportTemplateController : ControllerBase
{
    private readonly IGlossaryImportTemplateService _templates;

    public GlobalGlossaryImportTemplateController(IGlossaryImportTemplateService templates)
    {
        _templates = templates;
    }

    [HttpGet]
    [RequirePermission(AdminPermissions.GlossaryRead)]
    public async Task<ActionResult<GlossaryImportTemplateDto>> Get(CancellationToken ct)
    {
        var result = await _templates.GetAsync(ct);
        return result.IsSuccess ? Ok(result.Value) : Failure(result);
    }

    /// <summary>Replaces the whole configuration. 400 with the first problem when it is not valid.</summary>
    [HttpPut]
    [AdminAudited(AdminAuditGlossaryActions.TemplateUpdated, AdminAuditEntityTypes.GlossaryImportTemplate, typeof(GlossaryImportTemplate))]
    [RequirePermission(AdminPermissions.GlossaryManage)]
    public async Task<ActionResult<GlossaryImportTemplateDto>> Update(
        [FromBody] UpdateGlossaryImportTemplateDto request, CancellationToken ct)
    {
        if (!TryGetActorId(out var actorId)) return Unauthorized();

        var result = await _templates.UpdateAsync(request, actorId, ct);
        return result.IsSuccess ? Ok(result.Value) : Failure(result);
    }

    /// <summary>Back to the built-in default. Answers the default, so the screen can show it.</summary>
    [HttpDelete]
    [AdminAudited(AdminAuditGlossaryActions.TemplateReset, AdminAuditEntityTypes.GlossaryImportTemplate, typeof(GlossaryImportTemplate))]
    [RequirePermission(AdminPermissions.GlossaryManage)]
    public async Task<ActionResult<GlossaryImportTemplateDto>> Reset(CancellationToken ct)
    {
        if (!TryGetActorId(out _)) return Unauthorized();

        var result = await _templates.ResetAsync(ct);
        return result.IsSuccess ? Ok(result.Value) : Failure(result);
    }

    private bool TryGetActorId(out Guid actorId)
    {
        var idString = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(idString, out actorId);
    }

    private ActionResult Failure(Result result) => result.ErrorCode switch
    {
        ErrorCodes.ValidationError => BadRequest(result.Error),
        _ => StatusCode(500, result.Error),
    };
}

/// <summary>
/// WT-880: the same configuration, read-only, for the workspace Glossary page's "Import template"
/// tab and the Import dialog's quick download. Any authenticated user (PO decision 2026-10-02:
/// every workspace member may view and download it) — it holds a file shape and sample values,
/// nothing workspace-owned, so there is no workspace to check membership of. Same reasoning as
/// <c>GET api/v1/glossaries/global</c>.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/glossaries/import-template")]
public class GlossaryImportTemplateController : ControllerBase
{
    private readonly IGlossaryImportTemplateService _templates;

    public GlossaryImportTemplateController(IGlossaryImportTemplateService templates)
    {
        _templates = templates;
    }

    [HttpGet]
    public async Task<ActionResult<GlossaryImportTemplateDto>> Get(CancellationToken ct)
    {
        var result = await _templates.GetAsync(ct);
        // Which staff member saved it is the admin console's business, not a workspace's.
        return result.IsSuccess ? Ok(result.Value! with { UpdatedBy = null }) : StatusCode(500, result.Error);
    }
}
