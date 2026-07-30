using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/outbox")]
[Authorize(Policy = "BillingAdmin")]
public sealed class OutboxAdminController(
    IUnitOfWork unitOfWork,
    OutboxReplayService replayService) : ControllerBase
{
    [HttpGet("dead-letters")]
    public async Task<IActionResult> GetDeadLetters([FromQuery] int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var messages = await unitOfWork.OutboxMessages.GetPagedAsync(
            message => message.DeadLetteredAt != null,
            0,
            limit,
            query => query.OrderByDescending(message => message.DeadLetteredAt),
            cancellationToken);
        return Ok(messages.Select(message => new
        {
            message.Id,
            message.EventType,
            message.SchemaVersion,
            message.AttemptCount,
            message.DeadLetteredAt,
            message.LastError,
            message.CorrelationId,
            message.WorkspaceId
        }));
    }

    [HttpPost("{eventId:guid}/replay")]
    public async Task<IActionResult> Replay(Guid eventId, CancellationToken cancellationToken)
        => await replayService.ReplayAsync(eventId, cancellationToken)
            ? Accepted(new { eventId, status = "queued" })
            : NotFound(new { eventId, error = "dead-letter event not found" });
}
