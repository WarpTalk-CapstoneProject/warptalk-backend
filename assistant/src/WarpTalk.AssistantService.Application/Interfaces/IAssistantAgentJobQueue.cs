using System;
using System.Collections.Generic;
using System.Threading;

namespace WarpTalk.AssistantService.Application.Interfaces;

public record AssistantAgentJob(Guid ConversationId, Guid AssistantMessageId, Guid WorkspaceId, Guid UserId);

/// <summary>
/// In-process job queue handed off from the HTTP request (which returns 202 immediately)
/// to a background worker that drives the agent loop. Backed by System.Threading.Channels
/// rather than a bare Task.Run so an in-flight job survives being picked up by a single
/// long-running BackgroundService instead of racing arbitrary thread-pool work items.
/// </summary>
public interface IAssistantAgentJobQueue
{
    ValueTask EnqueueAsync(AssistantAgentJob job, CancellationToken ct = default);
    IAsyncEnumerable<AssistantAgentJob> DequeueAllAsync(CancellationToken ct = default);
}
