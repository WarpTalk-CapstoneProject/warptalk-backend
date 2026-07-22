using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;

namespace WarpTalk.AssistantService.API.Services;

/// <summary>
/// Dequeues agent jobs enqueued by AssistantConversationService.SendMessageAsync and runs
/// each one's AssistantAgentLoop in its own DI scope. Jobs run concurrently (fire-and-forget
/// per job) so one slow conversation never blocks another's reply from streaming.
/// </summary>
public class AssistantAgentBackgroundWorker : BackgroundService
{
    private readonly IAssistantAgentJobQueue _jobQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AssistantAgentBackgroundWorker> _logger;

    public AssistantAgentBackgroundWorker(
        IAssistantAgentJobQueue jobQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<AssistantAgentBackgroundWorker> logger)
    {
        _jobQueue = jobQueue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _jobQueue.DequeueAllAsync(stoppingToken))
        {
            _ = ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(AssistantAgentJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        try
        {
            var loop = scope.ServiceProvider.GetRequiredService<AssistantAgentLoop>();
            await loop.RunAsync(job, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AssistantAgentBackgroundWorker: unhandled failure processing job for message {MessageId}.", job.AssistantMessageId);
        }
    }
}
