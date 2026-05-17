using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

public class ArtifactsFinalizationWorker : BackgroundService
{
    private readonly IArtifactsFinalizationQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ArtifactsFinalizationWorker> _logger;

    public ArtifactsFinalizationWorker(
        IArtifactsFinalizationQueue queue,
        IServiceProvider serviceProvider,
        ILogger<ArtifactsFinalizationWorker> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ArtifactsFinalizationWorker starting...");

        try
        {
            await foreach (var roomId in _queue.ReadAllAsync(stoppingToken))
            {
                // Run finalization asynchronously on a thread pool thread to avoid blocking the queue reader
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var finalizationService = scope.ServiceProvider.GetRequiredService<IArtifactsFinalizationService>();
                        await finalizationService.ProcessRoomFinalizationAsync(roomId, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing finalization for room {RoomId}", roomId);
                    }
                }, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error reading from finalization channel");
        }

        _logger.LogInformation("ArtifactsFinalizationWorker stopping.");
    }
}
