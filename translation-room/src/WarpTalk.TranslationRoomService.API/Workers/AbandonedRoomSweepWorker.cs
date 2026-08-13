using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// Ends meetings that nobody is in.
///
/// THE GAP THIS FILLS
///     Nothing ends a room when the last person leaves. A room reaches ENDED only when a host
///     presses "End for everyone", which is a two-call client-side saga with no server-side
///     reconciliation — EndTranslationRoomAsync's own comment says a host transfer or "a network
///     blip between the two calls leaves the same orphan". And a browser that simply closed tells
///     nobody anything.
///
///     So rooms accumulate in IN_PROGRESS forever. Production is showing meetings from 9 August
///     as LIVE NOW. They never reach History, they keep claiming occupancy, and their transcript
///     and summary are never finalized — because finalization is queued by ending.
///
///     ExpireTranslationRoomAsync looks like the cure and is not: it only moves SCHEDULED or
///     WAITING rooms to EXPIRED, cannot touch IN_PROGRESS, and has no production callers.
///
/// HOW IT ENDS THEM
///     Through <see cref="ITranslationRoomService.EndTranslationRoomAsync"/> with the room's own
///     HostId — the same call the host's own button makes. Deliberately not a status write of its
///     own: ending a room also releases participants, stops the AI routes, publishes the room's
///     end and queues artifact finalization, and a second implementation of that would drift from
///     the first. The sweep decides WHICH rooms; the service decides what ending means.
///
/// WHY THE STATE LIVES IN REDIS
///     "Empty for 20 minutes" needs to know when it became empty, and no column records that. The
///     first sweep to find a room empty writes the timestamp; a later one ends it. The key
///     carries a TTL comfortably past the grace period, so nothing accumulates and a restart
///     costs at most one extra grace period rather than losing the room again.
/// </summary>
public class AbandonedRoomSweepWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<AbandonedRoomSweepWorker> _logger;

    private readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Statuses a meeting can be abandoned in. SCHEDULED is absent on purpose — a scheduled room
    /// has nobody in it by definition and is not abandoned, it has not started.
    /// </summary>
    private static readonly string[] LiveStatuses = { "IN_PROGRESS", "WAITING", "PAUSED" };

    /// <summary>
    /// Old enough to be somebody else's problem. A room left open a month ago is not going to be
    /// rejoined, and ending it would republish its artifacts into Knowledge as if it just
    /// happened. Anything older than this needs a deliberate backfill, not a background sweep.
    /// </summary>
    private readonly TimeSpan _lookback = TimeSpan.FromDays(7);

    /// <summary>One sweep must not stampede the finalizer, which holds a semaphore of 4.</summary>
    private const int MaxRoomsPerSweep = 20;

    public AbandonedRoomSweepWorker(
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        ILogger<AbandonedRoomSweepWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AbandonedRoomSweepWorker started. Sweeping every {Interval} for live rooms empty for over {Grace}.",
            _sweepInterval,
            AbandonedRoomPolicy.GracePeriod);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep must not take the worker down: the next one re-derives the same
                // list from the database and tries again.
                _logger.LogError(ex, "Abandoned room sweep failed.");
            }

            try
            {
                await Task.Delay(_sweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var roomService = scope.ServiceProvider.GetRequiredService<ITranslationRoomService>();

        var now = DateTime.UtcNow;
        var startedAfter = now - _lookback;

        var live = await unitOfWork.TranslationRoomRepository.FindAsync(
            room =>
                LiveStatuses.Contains(room.Status)
                && room.DeletedAt == null
                && room.IsActive
                && (room.StartedAt == null || room.StartedAt > startedAfter),
            "",
            ct);

        if (live.Count == 0) return;

        var occupancy = await unitOfWork.TranslationRoomParticipantRepository
            .CountSeatHoldingParticipantsByRoomsAsync(live.Select(room => room.Id).ToList(), ct);

        var db = _redis.GetDatabase();
        var ended = 0;

        foreach (var room in live)
        {
            if (ended >= MaxRoomsPerSweep)
            {
                // Said out loud. A silent cap looks exactly like "there was nothing left to do".
                _logger.LogInformation(
                    "Abandoned-room cap of {Cap} reached; the rest wait for the next sweep.",
                    MaxRoomsPerSweep);
                break;
            }

            var key = $"translationRoom:{room.Id}:empty_since";
            var seatHolders = occupancy.GetValueOrDefault(room.Id);
            var stored = await db.StringGetAsync(key);
            DateTime? emptySince = stored.HasValue
                && DateTime.TryParse(
                    stored.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed)
                    ? parsed
                    : null;

            switch (AbandonedRoomPolicy.Decide(seatHolders, emptySince, now))
            {
                case AbandonedRoomAction.Leave when seatHolders > 0:
                    // Somebody came back. Forget the observation so a later emptying starts a
                    // fresh grace rather than inheriting the old one and ending immediately.
                    await db.KeyDeleteAsync(key);
                    continue;

                case AbandonedRoomAction.Leave:
                    continue;

                case AbandonedRoomAction.StartGrace:
                    await db.StringSetAsync(
                        key,
                        now.ToString("O", CultureInfo.InvariantCulture),
                        AbandonedRoomPolicy.GracePeriod + TimeSpan.FromHours(1));
                    _logger.LogInformation(
                        "Room {RoomId} is empty; starting the {Grace} grace before ending it.",
                        room.Id,
                        AbandonedRoomPolicy.GracePeriod);
                    continue;
            }

            // Ended through the service, with the room's own host, so this takes exactly the path
            // the host's own "End for everyone" takes — participants released, routes stopped,
            // artifact finalization queued.
            var result = await roomService.EndTranslationRoomAsync(room.Id, room.HostId, ct);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Could not end abandoned room {RoomId}: {Error} ({ErrorCode}).",
                    room.Id,
                    result.Error,
                    result.ErrorCode);
                continue;
            }

            await db.KeyDeleteAsync(key);
            ended++;

            _logger.LogInformation(
                "Ended abandoned room {RoomId}. It was {Status} with nobody in it since {EmptySince}.",
                room.Id,
                room.Status,
                emptySince);
        }
    }
}
