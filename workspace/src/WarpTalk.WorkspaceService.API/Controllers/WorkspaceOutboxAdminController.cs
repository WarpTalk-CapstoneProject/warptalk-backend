using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarpTalk.Shared.Authorization;
using WarpTalk.WorkspaceService.Infrastructure.Outbox;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces/outbox")]
// Platform-wide: lists and replays dead letters from EVERY workspace. `Roles = "Admin"` admitted
// the workspace-administrator role, which auth.user_roles also hands out globally (the seeded demo
// accounts carry it), so a tenant admin could read other tenants' event payload errors and
// re-publish their events. The system-admin policy is the gate every other admin surface uses.
public sealed class WorkspaceOutboxAdminController(
    WorkspaceDbContext dbContext,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("dead-letters")]
    [RequirePermission(AdminPermissions.HealthRead)]
    public async Task<IActionResult> GetDeadLetters(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var messages = await dbContext.WorkspaceOutboxMessages
            .AsNoTracking()
            .Where(message => message.DeadLetteredAt != null)
            .OrderByDescending(message => message.DeadLetteredAt)
            .Take(limit)
            .Select(message => new
            {
                message.Id,
                message.EventType,
                message.SchemaVersion,
                message.AttemptCount,
                message.DeadLetteredAt,
                message.LastError,
                message.CorrelationId,
                message.WorkspaceId
            })
            .ToListAsync(cancellationToken);
        return Ok(messages);
    }

    [HttpPost("{eventId:guid}/replay")]
    [RequirePermission(AdminPermissions.HealthOperate)]
    public async Task<IActionResult> Replay(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var message = await dbContext.WorkspaceOutboxMessages
            .SingleOrDefaultAsync(
                candidate => candidate.Id == eventId && candidate.DeadLetteredAt != null,
                cancellationToken);
        if (message is null)
            return NotFound(new { eventId, error = "dead-letter event not found" });

        message.AttemptCount = 0;
        message.AvailableAt = timeProvider.GetUtcNow().UtcDateTime;
        message.LockedAt = null;
        message.DeadLetteredAt = null;
        message.LastError = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        WorkspaceOutboxMetrics.Replayed.Add(
            1,
            new KeyValuePair<string, object?>("event.type", message.EventType));

        return Accepted(new { eventId, status = "queued" });
    }
}
