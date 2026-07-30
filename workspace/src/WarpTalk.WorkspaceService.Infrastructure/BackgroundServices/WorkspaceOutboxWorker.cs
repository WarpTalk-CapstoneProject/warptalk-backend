using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Infrastructure.Outbox;

namespace WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

public sealed class WorkspaceOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkspaceOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurgeAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider
                    .GetRequiredService<WorkspaceOutboxDispatcher>();
                var published = await dispatcher.DispatchPendingAsync(100, stoppingToken);
                if (published > 0)
                {
                    logger.LogInformation(
                        "Published {Count} workspace outbox event(s).",
                        published);
                }

                var now = DateTimeOffset.UtcNow;
                if (now >= nextPurgeAt)
                {
                    var purged = await dispatcher.PurgePublishedBeforeAsync(
                        now.AddDays(-7).UtcDateTime,
                        stoppingToken);
                    if (purged > 0)
                    {
                        logger.LogInformation(
                            "Purged {Count} published workspace outbox event(s).",
                            purged);
                    }
                    nextPurgeAt = now.AddHours(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Workspace outbox dispatch cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
