using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// The workspace-service actions of the admin workspace page (ERP-style detail): transfer
/// ownership, send the owner a notice, internal notes, the timeline, and the data-summary export.
///
/// Same gate as <see cref="AdminWorkspacesController"/> — the shared system-admin policy — and the
/// same rule: the actor is taken from the token, never from a body. Every write takes a reason (a
/// note is its own) and lands in the platform audit log.
/// </summary>
[ApiController]
[Route("api/v1/admin/workspaces/{id:guid}")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminWorkspaceActionsController : ControllerBase
{
    private readonly IAdminWorkspaceActionService _service;

    public AdminWorkspaceActionsController(IAdminWorkspaceActionService service)
    {
        _service = service;
    }

    [HttpPost("transfer-ownership")]
    public Task<IActionResult> TransferOwnership(Guid id, [FromBody] AdminTransferOwnershipRequest request, CancellationToken ct)
        => ActAsync(actor => _service.TransferOwnershipAsync(id, request, actor.ActorId, actor.CorrelationId, ct));

    [HttpPost("notices")]
    public Task<IActionResult> SendNotice(Guid id, [FromBody] AdminWorkspaceNoticeRequest request, CancellationToken ct)
        => ActAsync(actor => _service.SendNoticeAsync(id, request, actor.ActorId, actor.CorrelationId, ct));

    [HttpPost("notes")]
    public Task<IActionResult> AddNote(Guid id, [FromBody] AdminAddWorkspaceNoteRequest request, CancellationToken ct)
        => ActAsync(actor => _service.AddNoteAsync(id, request, actor.ActorId, actor.CorrelationId, ct));

    [HttpGet("timeline")]
    public async Task<IActionResult> GetTimeline(Guid id, [FromQuery] int? limit, CancellationToken ct)
        => ToActionResult(await _service.GetTimelineAsync(id, limit, ct));

    /// <summary>POST, not GET: an export carries a reason and is itself an audited action.</summary>
    [HttpPost("export")]
    public Task<IActionResult> Export(Guid id, [FromBody] AdminWorkspaceExportRequest request, CancellationToken ct)
        => ActAsync(actor => _service.ExportAsync(id, request, actor.ActorId, actor.CorrelationId, ct));

    private async Task<IActionResult> ActAsync<T>(Func<AdminActorContext, Task<Result<T>>> action)
    {
        if (!AdminActorContext.TryResolve(User, HttpContext, out var actor))
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        return ToActionResult(await action(actor));
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        var error = new ApiErrorResponse(result.Error, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(error),
            ErrorCodes.Forbidden => StatusCode(403, error),
            ErrorCodes.Conflict => Conflict(error),
            ErrorCodes.ValidationError => BadRequest(error),
            _ => StatusCode(500, error),
        };
    }
}
