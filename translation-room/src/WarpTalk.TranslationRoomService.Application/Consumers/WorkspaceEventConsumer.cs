using MassTransit;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using WarpTalk.Shared.Events;

namespace WarpTalk.TranslationRoomService.Application.Consumers;

/// <summary>
/// Consumes domain events from the Workspace Service to enforce cross-service business rules 
/// like cascading deletions and realtime member evictions.
/// </summary>
public class WorkspaceEventConsumer : 
    IConsumer<WorkspaceDeletedEvent>,
    IConsumer<MemberRemovedEvent>
{
    private readonly ILogger<WorkspaceEventConsumer> _logger;

    public WorkspaceEventConsumer(ILogger<WorkspaceEventConsumer> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<WorkspaceDeletedEvent> context)
    {
        _logger.LogInformation("Received WorkspaceDeletedEvent for Workspace: {WorkspaceId}", context.Message.WorkspaceId);

        // TODO: 
        // 1. Fetch all TranslationRooms where WorkspaceId == context.Message.WorkspaceId and Status == IN_PROGRESS
        // 2. Mark them as CANCELLED in DB
        // 3. Publish a ForceDisconnectRoom command to the Redis Backplane (Gateway)
        
        await Task.CompletedTask;
    }

    public async Task Consume(ConsumeContext<MemberRemovedEvent> context)
    {
        _logger.LogInformation("Received MemberRemovedEvent for User: {UserId} in Workspace: {WorkspaceId}", 
            context.Message.UserId, context.Message.WorkspaceId);

        // TODO: 
        // 1. Fetch active meeting participants for this user
        // 2. Mark participant status as KICKED
        // 3. Publish a KickUser command to the Redis Backplane (Gateway)

        await Task.CompletedTask;
    }
}
