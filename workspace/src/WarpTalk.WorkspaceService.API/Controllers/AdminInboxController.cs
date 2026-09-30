using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Services;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// The pending-work inbox (G12, /admin/inbox): everything waiting on platform staff, aggregated live
/// from the services that own it. Each source only contributes if the caller's role can see it, and a
/// source that is down is reported rather than failing the page. Triage writes (assign, snooze, done,
/// reopen, notes) are recorded in the admin audit log by the service, in the same save.
///
/// Item keys contain ':' and so travel in the body or query string, never the path.
/// </summary>
[ApiController]
[Route("api/v1/admin/inbox")]
public sealed class AdminInboxController : ControllerBase
{
    private readonly IAdminInboxService _inbox;

    public AdminInboxController(IAdminInboxService inbox)
    {
        _inbox = inbox;
    }

    /// <summary><c>?refresh=true</c> skips the 30-second per-person cache.</summary>
    [HttpGet]
    [RequirePermission(AdminPermissions.InboxRead)]
    public async Task<IActionResult> Get([FromQuery] bool refresh = false, CancellationToken ct = default)
        => ToActionResult(await _inbox.GetAsync(User, refresh, ct));

    /// <summary>Counts for the sidebar badge, from the same cache.</summary>
    [HttpGet("summary")]
    [RequirePermission(AdminPermissions.InboxRead)]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
        => ToActionResult(await _inbox.GetSummaryAsync(User, ct));

    [HttpGet("notes")]
    [RequirePermission(AdminPermissions.InboxRead)]
    public async Task<IActionResult> GetNotes([FromQuery] string key, CancellationToken ct)
        => ToActionResult(await _inbox.GetNotesAsync(key, ct));

    /// <summary>assigneeId null unassigns.</summary>
    [HttpPost("assign")]
    [RequirePermission(AdminPermissions.InboxManage)]
    public async Task<IActionResult> Assign([FromBody] AdminInboxAssignRequest request, CancellationToken ct)
        => ToActionResult(await _inbox.AssignAsync(User, request, Correlation(), ct));

    /// <summary>until null wakes the item up.</summary>
    [HttpPost("snooze")]
    [RequirePermission(AdminPermissions.InboxManage)]
    public async Task<IActionResult> Snooze([FromBody] AdminInboxSnoozeRequest request, CancellationToken ct)
        => ToActionResult(await _inbox.SnoozeAsync(User, request, Correlation(), ct));

    /// <summary>Only for items whose source has no natural completion; 409 otherwise.</summary>
    [HttpPost("done")]
    [RequirePermission(AdminPermissions.InboxManage)]
    public async Task<IActionResult> MarkDone([FromBody] AdminInboxKeyRequest request, CancellationToken ct)
        => ToActionResult(await _inbox.MarkDoneAsync(User, request, Correlation(), ct));

    [HttpPost("reopen")]
    [RequirePermission(AdminPermissions.InboxManage)]
    public async Task<IActionResult> Reopen([FromBody] AdminInboxKeyRequest request, CancellationToken ct)
        => ToActionResult(await _inbox.ReopenAsync(User, request, Correlation(), ct));

    [HttpPost("notes")]
    [RequirePermission(AdminPermissions.InboxManage)]
    public async Task<IActionResult> AddNote([FromBody] AdminInboxNoteRequest request, CancellationToken ct)
        => ToActionResult(await _inbox.AddNoteAsync(User, request, Correlation(), ct));

    private string? Correlation()
    {
        var value = HttpContext.Request.Headers["X-Correlation-ID"].ToString();
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
            ErrorCodes.NotFound => NotFound(error),
            ErrorCodes.ValidationError => BadRequest(error),
            ErrorCodes.Conflict => Conflict(error),
            _ => StatusCode(StatusCodes.Status500InternalServerError, error),
        };
    }
}
