using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// Finds meetings that ended and never got their transcript and summary, and finalizes them.
///
/// WHY THIS IS NEEDED WHEN ArtifactsRecoveryWorker ALREADY EXISTS
///     That worker recovers a specific failure: audio routes sitting in SAVE_FAILED. It is
///     keyed on the route state machine, so it only sees rooms that got far enough to fail
///     inside it. The failures it cannot see are the ones where finalization never ran at all:
///
///       ArtifactsFinalizationQueue is an in-memory Channel&lt;Guid&gt;. A deploy, a restart, a
///       crash or an OOM kill between "meeting ended" and "artifacts written" drops every
///       queued room on the floor, and nothing anywhere records that they were ever queued.
///
///       ArtifactsFinalizationWorker.ProcessRoomAsync catches every exception, logs it, and
///       moves on. The room is never retried and no route is marked SAVE_FAILED.
///
///       A room with no audio routes has nothing for a route-keyed sweep to find.
///
///     In all three the meeting is over, the transcript exists in TranscriptService, and the
///     room page polls "Waiting for post-meeting artifacts" forever. That is the report: the
///     summary and transcript never arrive and there is no way to ask for them again.
///
/// WHAT IT KEYS ON INSTEAD
///     The durable fact, read straight from the database: a room in a TERMINAL status that has
///     NO artifacts. FinalizeRoomArtifactsAsync always writes exactly two (transcript and
///     summary) or throws, so "terminal and zero artifacts" means finalization did not
///     complete — whatever the reason, and whether or not this process was the one that tried.
///     Nothing in memory is consulted, so a restart cannot lose the signal; the query rebuilds
///     it every sweep.
///
/// WHY IT IS BOUNDED
///     A room CAN legitimately finalize to nothing forever if the underlying failure is
///     permanent, and a sweep with no memory would re-queue it every five minutes until the
///     end of time. Attempts are counted in Redis with a TTL — the same shape and lifetime
///     ArtifactsRecoveryWorker already uses for its own counter — so the count survives a
///     restart, which is exactly the case this worker exists for.
/// </summary>
public class ArtifactsReconciliationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IArtifactsFinalizationQueue _queue;
    private readonly IConnectionMultiplexer _redis;
    private readonly ArtifactFinalizationSettings _settings;
    private readonly ILogger<ArtifactsReconciliationWorker> _logger;

    private readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after a meeting ends before this worker considers it abandoned.
    ///
    /// Long enough that the normal path — queue, transcript gRPC, summary, save — is not raced
    /// and a healthy finalization is never queued twice. Short enough that somebody who leaves
    /// the ended page and comes back to it has their artifacts by then.
    /// </summary>
    private readonly TimeSpan _gracePeriod = TimeSpan.FromMinutes(10);

    /// <summary>Old enough to be somebody else's problem. A meeting from last month is not
    /// waiting on a summary, and re-finalizing history would republish it to Knowledge.</summary>
    private readonly TimeSpan _lookback = TimeSpan.FromDays(7);

    /// <summary>One sweep must not stampede the finalizer, which holds a semaphore of 4 and
    /// makes a gRPC call per room.</summary>
    private const int MaxRoomsPerSweep = 20;

    public ArtifactsReconciliationWorker(
        IServiceProvider serviceProvider,
        IArtifactsFinalizationQueue queue,
        IConnectionMultiplexer redis,
        IOptions<ArtifactFinalizationSettings> options,
        ILogger<ArtifactsReconciliationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _queue = queue;
        _redis = redis;
        _settings = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ArtifactsReconciliationWorker started. Sweeping every {Interval} for meetings that ended over {Grace} ago with no artifacts.",
            _sweepInterval,
            _gracePeriod);

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
                // A failed sweep must not take the worker down: the next one re-derives the
                // same list from the database and tries again.
                _logger.LogError(ex, "Artifacts reconciliation sweep failed.");
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

        var now = DateTime.UtcNow;
        var queuedBefore = now - _gracePeriod;
        var endedAfter = now - _lookback;

        // EndedAt is the only honest clock here. A room's UpdatedAt moves for reasons that have
        // nothing to do with the meeting being over.
        var abandoned = await unitOfWork.TranslationRoomRepository.FindAsync(
            room =>
                TranslationRoomConstants.TerminalStatuses.Contains(room.Status)
                && room.EndedAt != null
                && room.EndedAt < queuedBefore
                && room.EndedAt > endedAfter
                && !room.TranslationRoomArtifacts.Any(),
            "TranslationRoomArtifacts",
            ct);

        if (abandoned.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Found {Count} ended meeting(s) with no artifacts; re-queueing finalization.",
            abandoned.Count);

        var db = _redis.GetDatabase();
        var queued = 0;

        foreach (var room in abandoned.OrderByDescending(room => room.EndedAt))
        {
            if (queued >= MaxRoomsPerSweep)
            {
                // Said out loud. A silent cap looks exactly like "there was nothing left to do",
                // and the difference matters when a backlog is being worked through.
                _logger.LogInformation(
                    "Reconciliation cap of {Cap} reached; {Remaining} room(s) deferred to the next sweep.",
                    MaxRoomsPerSweep,
                    abandoned.Count - queued);
                break;
            }

            var attemptsKey = $"translationRoom:{room.Id}:reconcile_attempts";
            var attempts = (int)await db.StringIncrementAsync(attemptsKey);
            if (attempts == 1)
            {
                await db.KeyExpireAsync(attemptsKey, _lookback);
            }

            switch (ArtifactsReconciliationPolicy.Decide(attempts, _settings.MaxRecoverySweeps))
            {
                case ReconciliationAction.AbandonAndWarn:
                    _logger.LogWarning(
                        "Room {RoomId} still has no artifacts after {Attempts} reconciliation attempts; giving up. Its meeting ended at {EndedAt}.",
                        room.Id,
                        _settings.MaxRecoverySweeps,
                        room.EndedAt);
                    continue;

                case ReconciliationAction.Skip:
                    continue;
            }

            _queue.QueueFinalization(room.Id);
            queued++;

            _logger.LogInformation(
                "Re-queued finalization for room {RoomId} (attempt {Attempt}/{Max}), ended {EndedAt}.",
                room.Id,
                attempts,
                _settings.MaxRecoverySweeps,
                room.EndedAt);
        }
    }
}
