using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.WorkspaceService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces")]
public class WorkspacesController : ControllerBase
{
    private readonly IWorkspaceService _workspaceService;

    /// <summary>
    /// The settings a PATCH may write. WT-646.
    ///
    /// PatchWorkspaceSettings merges the caller's JsonObject key-by-key into the current settings
    /// document, and until this allowlist existed it merged ANY key — including the four computed,
    /// read-only fields the GET response carries back out (maxActiveRoomsCeiling and its Source,
    /// maxLanguagesCeiling and its Source). Those are the plan's ceilings, resolved from billing
    /// entitlements on every read; they are not settings and there is no honest way to set them.
    ///
    /// They were never actually PERSISTED — WorkspaceSettingsDto.ToConfiguration enumerates the
    /// fields it copies and WorkspaceConfiguration has no ceiling properties, so a forged ceiling
    /// died at the mapper. What it did reach was the 200 response body, which is the merged DTO:
    /// a client could PATCH maxActiveRoomsCeiling: 999 and be told, by the server, that its
    /// ceiling was 999. Cosmetic today, and one refactor away from not being — the allowlist is
    /// what stops the next person who makes that mapper reflective from opening a real hole.
    ///
    /// An ALLOWLIST rather than a denylist of the four known computed keys, so a field added to
    /// the DTO in future is unwritable until someone deliberately lists it here.
    /// </summary>
    private static readonly HashSet<string> PatchableSettingsKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "defaultLanguage",
        "timezone",
        "allowedTargetLanguages",
        "voiceCloningEnabled",
        "maxActiveRooms",
        "artifactRetentionDays",
        // A display mirror of workspace_verified_domains rather than the record itself, but it
        // stays patchable: clients read-modify-write the whole document, and the service already
        // decides what a change to it is allowed to mean.
        "verifiedDomains",
        "allowExternalCollaboration",
        // Derived from the verified-domain list, and UpdateWorkspaceSettingsAsync rejects a
        // DIFFERENT value rather than the field's presence. It must stay patchable for exactly
        // that reason — a client echoing back what GET gave it must not be an error.
        "requireVerifiedDomainForInternal",
        "aiUsagePolicy",
        "isProfanityFilterEnabled",
        "invitationExpiryDays",
        "allowAnyPlugins"
    };

    public WorkspacesController(IWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> CreateWorkspace([FromBody] CreateWorkspaceRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceService.CreateWorkspaceAsync(request, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetWorkspaces([FromQuery] GetWorkspacesQuery query, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceService.GetWorkspacesAsync(query, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [Authorize]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetWorkspaceById(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = User.IsInRole("admin")
            ? await _workspaceService.GetWorkspaceByIdForAdminAsync(id, ct)
            : await _workspaceService.GetWorkspaceByIdAsync(id, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("{id:guid}/select")]
    public async Task<IActionResult> SelectWorkspace(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceService.SelectWorkspaceAsync(id, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [Authorize]
    [HttpGet("{id:guid}/settings")]
    public async Task<IActionResult> GetWorkspaceSettings(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceService.GetWorkspaceSettingsAsync(id, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [Authorize]
    [HttpPut("{id:guid}/settings")]
    public async Task<IActionResult> UpdateWorkspaceSettings(Guid id, [FromBody] WorkspaceSettingsDto settings, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(id, settings, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return NoContent();
    }

    [Authorize]
    [HttpPatch("{id:guid}/settings")]
    public async Task<IActionResult> PatchWorkspaceSettings(Guid id, [FromBody] JsonObject patch, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));
        if (patch == null) return BadRequest(new ApiErrorResponse("Invalid settings payload.", ErrorCodes.ValidationError));

        // Refuse before reading anything. An unwritable key is a malformed request, not a
        // permission question, so it does not need the workspace loaded to answer — and naming the
        // keys beats the old behaviour of merging them and discarding them further down, which was
        // indistinguishable from success.
        var rejectedKeys = patch
            .Select(property => property.Key)
            .Where(key => !PatchableSettingsKeys.Contains(key))
            .ToList();
        if (rejectedKeys.Count > 0)
        {
            return BadRequest(new ApiErrorResponse(
                string.Format(
                    CultureInfo.InvariantCulture,
                    WorkspaceConstants.Errors.SettingsPatchKeyNotWritableFormat,
                    string.Join(", ", rejectedKeys)),
                ErrorCodes.ValidationError));
        }

        var current = await _workspaceService.GetWorkspaceSettingsAsync(id, userId.Value, ct);
        if (!current.IsSuccess)
        {
            if (current.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(current.Error, current.ErrorCode));
            if (current.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(current.Error, current.ErrorCode));
            return BadRequest(new ApiErrorResponse(current.Error, current.ErrorCode));
        }

        var mergedNode = JsonSerializer.SerializeToNode(current.Value)!.AsObject();
        foreach (var property in patch)
            mergedNode[property.Key] = property.Value?.DeepClone();

        var merged = mergedNode.Deserialize<WorkspaceSettingsDto>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (merged == null) return BadRequest(new ApiErrorResponse("Invalid settings payload.", ErrorCodes.ValidationError));

        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(id, merged, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(merged);
    }

    [Authorize]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteWorkspace(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceService.SoftDeleteWorkspaceAsync(id, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            // WT-434 (Linear): without this branch a server-side failure fell through to 400,
            // which reads as "you sent a bad request" for a request that was perfectly formed —
            // the EF tracking bug in SoftDeleteWorkspaceAsync hid behind that label.
            if (result.ErrorCode == ErrorCodes.InternalServerError)
                return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return NoContent();
    }
}
