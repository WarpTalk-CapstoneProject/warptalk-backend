using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Infrastructure.Outbox;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces/outbox")]
[Authorize(Roles = "Admin")]
public sealed class WorkspaceOutboxAdminController(
    WorkspaceDbContext dbContext,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("dead-letters")]
    public async Task<IActionResult> GetDeadLetters(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var messages = await dbContext.OutboxMessages
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
    public async Task<IActionResult> Replay(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var message = await dbContext.OutboxMessages
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
