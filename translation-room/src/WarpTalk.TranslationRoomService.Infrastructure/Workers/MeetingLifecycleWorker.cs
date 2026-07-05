using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Infrastructure.Workers;

/// <summary>
/// Background service responsible for cleaning up "Ghost Meetings" and handling Host Grace Periods.
/// Scans for IN_PROGRESS rooms with 0 active connections for over 5 minutes and terminates them.
/// </summary>
public class MeetingLifecycleWorker : BackgroundService
{
    private readonly ILogger<MeetingLifecycleWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _ghostThreshold = TimeSpan.FromMinutes(5);

    public MeetingLifecycleWorker(ILogger<MeetingLifecycleWorker> logger, IServiceProvider serviceProvider, IConnectionMultiplexer redis)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _redis = redis;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MeetingLifecycleWorker is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DetectAndCloseGhostMeetingsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while executing MeetingLifecycleWorker.");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private async Task DetectAndCloseGhostMeetingsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Scanning for Ghost Meetings...");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<WarpTalk.TranslationRoomService.Domain.Interfaces.IUnitOfWork>();
            var db = _redis.GetDatabase();

            var inProgressRooms = await unitOfWork.TranslationRoomRepository.FindAsync(
                r => r.Status == WarpTalk.TranslationRoomService.Domain.Enums.RoomStatus.IN_PROGRESS.ToString(),
                ct: cancellationToken);

            if (inProgressRooms == null || inProgressRooms.Count == 0) return;

            foreach (var room in inProgressRooms)
            {
                var participants = await unitOfWork.TranslationRoomParticipantRepository.FindAsync(
                    p => p.TranslationRoomId == room.Id && p.Status == WarpTalk.TranslationRoomService.Domain.Enums.TranslationRoomParticipantStatus.CONNECTED.ToString(),
                    ct: cancellationToken);

                // If active connections == 0 and time since start is > ghostThreshold
                if ((participants == null || participants.Count == 0) && (DateTime.UtcNow - room.StartedAt) > _ghostThreshold)
                {
                    _logger.LogWarning("Ghost meeting detected: Room {RoomId}. Terminating.", room.Id);

                    room.Status = WarpTalk.TranslationRoomService.Domain.Enums.RoomStatus.ENDED.ToString();
                    room.EndedAt = DateTime.UtcNow;
                    unitOfWork.TranslationRoomRepository.Update(room);

                    // Note: If we had a BillingService, we would publish a RoomCompletedEvent here to finalize charges.
                }
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning ghost meetings.");
        }
    }
}
