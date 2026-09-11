using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

public class IdleRoomMonitoringWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<IdleRoomMonitoringWorker> _logger;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public IdleRoomMonitoringWorker(
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        ILogger<IdleRoomMonitoringWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _redis = redis;
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

    /// <summary>One tick. Internal so the tests can drive it directly — see InternalsVisibleTo.</summary>
    internal async Task CheckAndEndIdleRoomsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var roomRepo = scope.ServiceProvider.GetRequiredService<ITranslationRoomRepository>();
        var participantRepo = scope.ServiceProvider.GetRequiredService<ITranslationRoomParticipantRepository>();
        var roomService = scope.ServiceProvider.GetRequiredService<ITranslationRoomService>();

        var now = DateTime.UtcNow;
        var db = _redis.GetDatabase();

        // Find all rooms that are WAITING or IN_PROGRESS
        var activeRooms = await roomRepo.FindAsync(r => r.Status == "WAITING" || r.Status == "IN_PROGRESS", "", ct);

        foreach (var room in activeRooms)
        {
            // Get participants
            var participants = await participantRepo.FindAsync(p => p.TranslationRoomId == room.Id, "", ct);

            // WT-263: "present in the room" has ONE definition, shared with the WT-262 capacity
            // check — TranslationRoomParticipantStatuses.SeatHolding (CONNECTED only) — minus the
            // one seat that is not a person: an EXTERNAL_BRIDGE room's far-side stand-in, which
            // holds its seat until End and has no socket that could release it. Counting it kept
            // every bridge room alive after its host left or crashed. See RoomPresence.
            var peopleInRoom = participants.Count(RoomPresence.IsPersonInRoom);

            // The idle clock starts when a tick first SEES the room empty, not at the
            // participants' last LeftAt. It used to fall back to JoinedAt when LeftAt was null, and
            // a dropped socket writes DISCONNECTED with no LeftAt — so a sole participant an hour
            // into a meeting who lost wifi for a second was "idle since they joined" and ended on
            // the very next tick. A bridge room is always a sole participant.
            var key = $"translationRoom:{room.Id}:idle_since";
            if (peopleInRoom > 0)
            {
                // Somebody is here (or came back). A later emptying starts a fresh grace.
                await EmptyRoomObservation.ClearAsync(db, key);
                continue;
            }

            var emptySince = await EmptyRoomObservation.ReadAsync(db, key);
            switch (AbandonedRoomPolicy.Decide(peopleInRoom, emptySince, now, _idleTimeout))
            {
                case AbandonedRoomAction.StartGrace:
                    await EmptyRoomObservation.StartAsync(db, key, now, _idleTimeout);
                    continue;

                case AbandonedRoomAction.Leave:
                    continue;
            }

            _logger.LogInformation("Room {RoomId} has had no participants since {IdleTime}. Auto-ending the room.", room.Id, emptySince);

            var result = await roomService.EndTranslationRoomAsync(room.Id, room.HostId, ct);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to auto-end room {RoomId}: {Error}", room.Id, result.Error);
                continue;
            }

            await EmptyRoomObservation.ClearAsync(db, key);
        }
    }
}
