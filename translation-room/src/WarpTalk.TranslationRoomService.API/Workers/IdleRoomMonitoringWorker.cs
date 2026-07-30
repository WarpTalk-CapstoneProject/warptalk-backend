using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

public class IdleRoomMonitoringWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IdleRoomMonitoringWorker> _logger;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public IdleRoomMonitoringWorker(
        IServiceProvider serviceProvider,
        ILogger<IdleRoomMonitoringWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IdleRoomMonitoringWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndEndIdleRoomsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in IdleRoomMonitoringWorker");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private async Task CheckAndEndIdleRoomsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var roomRepo = scope.ServiceProvider.GetRequiredService<ITranslationRoomRepository>();
        var participantRepo = scope.ServiceProvider.GetRequiredService<ITranslationRoomParticipantRepository>();
        var roomService = scope.ServiceProvider.GetRequiredService<ITranslationRoomService>();

        // Find all rooms that are WAITING or IN_PROGRESS
        var activeRooms = await roomRepo.FindAsync(r => r.Status == "WAITING" || r.Status == "IN_PROGRESS", "", ct);
        
        foreach (var room in activeRooms)
        {
            // Get participants
            var participants = await participantRepo.FindAsync(p => p.TranslationRoomId == room.Id, "", ct);
            var participantList = participants.ToList();

            var hasConnectedParticipants = participantList.Any(p =>
                p.Status == "CONNECTED" ||
                p.Status == "JOINED");

            if (!hasConnectedParticipants)
            {
                // Anchor the idle clock to when the room was last actually occupied — the
                // most recent LeftAt (or JoinedAt, if someone disconnected without a formal
                // leave) across participants — not a participant row's generic UpdatedAt.
                // UpdatedAt can be bumped by unrelated edits (e.g. a language preference
                // change) while nobody is present, which would wrongly keep resetting the
                // timer and prevent an empty room from ever auto-ending.
                DateTime lastPresentTime = room.StartedAt ?? room.CreatedAt;
                var departureTimes = participantList
                    .Select(p => p.LeftAt ?? p.JoinedAt)
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value);
                if (departureTimes.Any())
                {
                    lastPresentTime = departureTimes.Max();
                }

                if (DateTime.UtcNow - lastPresentTime > _idleTimeout)
                {
                    _logger.LogInformation("Room {RoomId} has had no participants since {IdleTime}. Auto-ending the room.", room.Id, lastPresentTime);
                    
                    var result = await roomService.EndTranslationRoomAsync(room.Id, room.HostId, ct);
                    if (!result.IsSuccess)
                    {
                        _logger.LogWarning("Failed to auto-end room {RoomId}: {Error}", room.Id, result.Error);
                    }
                }
            }
        }
    }
}
