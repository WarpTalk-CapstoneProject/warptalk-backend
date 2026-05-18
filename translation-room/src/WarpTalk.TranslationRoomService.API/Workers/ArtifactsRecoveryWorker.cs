using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

public class ArtifactsRecoveryWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ArtifactsRecoveryWorker> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(5);
    private readonly WarpTalk.TranslationRoomService.Domain.Configuration.ArtifactFinalizationSettings _settings;

    public ArtifactsRecoveryWorker(
        IServiceProvider serviceProvider,
        ILogger<ArtifactsRecoveryWorker> logger,
        IConnectionMultiplexer redis,
        Microsoft.Extensions.Options.IOptions<WarpTalk.TranslationRoomService.Domain.Configuration.ArtifactFinalizationSettings> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _redis = redis;
        _settings = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Artifacts Recovery Worker started. Sweeping every {Interval}", _sweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepFailedArtifactsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during artifacts recovery sweep.");
            }

            await Task.Delay(_sweepInterval, stoppingToken);
        }
    }

    private async Task SweepFailedArtifactsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITranslationRoomAudioRouteRepository>();
        var eventProcessor = scope.ServiceProvider.GetRequiredService<IAudioRouteEventProcessorService>();
        
        var db = _redis.GetDatabase();

        var failedRoutes = await repository.GetRoutesByStatusAsync(AudioRouteStatus.FINALIZING_ARTIFACTS_FAILED, ct);

        if (!failedRoutes.Any())
        {
            return;
        }

        // Distinct by Room ID in case multiple routes exist for the same room
        var failedRoomIds = failedRoutes.Select(r => r.TranslationRoomId).Distinct().ToList();

        _logger.LogInformation("Found {Count} rooms in FINALIZING_ARTIFACTS_FAILED state for recovery", failedRoomIds.Count);

        foreach (var roomId in failedRoomIds)
        {
            try
            {
                string attemptsKey = $"translationRoom:{roomId}:recovery_attempts";
                var attemptsVal = await db.StringGetAsync(attemptsKey);
                int attempts = attemptsVal.HasValue ? (int)attemptsVal : 0;

                if (attempts >= _settings.MaxRecoverySweeps)
                {
                    _logger.LogWarning("Room {RoomId} exhausted {MaxAttempts} recovery attempts. Emitting finalization_abandoned to conclude lifecycle.", roomId, _settings.MaxRecoverySweeps);
                    
                    await eventProcessor.ProcessEventAsync(
                        roomId, 
                        null, 
                        AudioRoutingEventType.finalization_abandoned.ToString(), 
                        "{}", 
                        ct);
                        
                    // Cleanup Redis counter
                    await db.KeyDeleteAsync(attemptsKey);
                }
                else
                {
                    attempts++;
                    await db.StringSetAsync(attemptsKey, attempts, TimeSpan.FromDays(7));
                    
                    _logger.LogInformation("Attempt {Attempt}/{MaxAttempts}: Pushing Room {RoomId} back to FINALIZING_ARTIFACTS queue.", attempts, _settings.MaxRecoverySweeps, roomId);
                    
                    await eventProcessor.ProcessEventAsync(
                        roomId, 
                        null, 
                        AudioRoutingEventType.flush_runtime.ToString(), 
                        "{}", 
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process recovery for room {RoomId}", roomId);
            }
        }
    }
}
