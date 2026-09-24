using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared.Coordination;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// WT-612 / WT-714 — the clock a booking runs on, both ends of it.
///
/// Before this, <c>scheduled_at</c> changed nothing about a room: it validated the form, drew the
/// calendar and timed the reminders, and the room stayed SCHEDULED until a human pressed Start. A
/// meeting booked for 14:00 therefore did not exist at 14:00, and whoever arrived first had to
/// know they were the one who had to open it — and a meeting nobody came to sat in SCHEDULED for
/// good, because <c>ExpireTranslationRoomAsync</c> had never had a caller.
///
/// Both edges run in one tick under one lease because they are one rule read from two sides
/// (<see cref="ScheduledOpeningEvaluator.MissedBookingGrace"/>): opening first, so a booking that
/// is due and a booking that is stale are both handled in the pass that finds them, and a room can
/// never be opened by one sweep a moment after another decided it was missed.
///
/// One poll a minute, the same cadence as <see cref="ReminderNotificationWorker"/> and
/// <see cref="IdleRoomMonitoringWorker"/>, and for the same reason: a minute is the resolution
/// people actually book meetings at, and the alternative — a timer per room — is a scheduler this
/// service does not have and would have to rebuild after every restart.
///
/// What it does NOT do is start the meeting. OPEN unlocks the door; the first person who walks
/// through takes the room to IN_PROGRESS (TranslationRoomService.JoinTranslationRoomAsync), which
/// is where translation sessions, audio routes and billable AI begin. An empty room that opened on
/// time costs nothing.
/// </summary>
public class ScheduledRoomLifecycleWorker : BackgroundService
{
    /// <summary>
    /// Its own lease, deliberately not <see cref="RoomEndingSweepLock"/>. That one serialises the
    /// sweeps that end rooms people were actually in; this one drives the booking clock, and a
    /// replica opening today's bookings has no reason to wait behind a replica closing yesterday's
    /// live meetings.
    /// </summary>
    internal const string SweepLockResource = "translation-room:scheduled-lifecycle-sweep";

    private readonly IServiceProvider _serviceProvider;
    private readonly IDistributedLockProvider _locks;
    private readonly ILogger<ScheduledRoomLifecycleWorker> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public ScheduledRoomLifecycleWorker(
        IServiceProvider serviceProvider,
        IDistributedLockProvider locks,
        ILogger<ScheduledRoomLifecycleWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _locks = locks;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduledRoomLifecycleWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _locks.TryRunExclusiveAsync(
                    SweepLockResource,
                    TimeSpan.FromMinutes(2),
                    SweepAsync,
                    _logger,
                    stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ScheduledRoomLifecycleWorker");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    /// <summary>One poll, both edges. Internal so the tests can drive it directly — see InternalsVisibleTo.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var roomService = scope.ServiceProvider.GetRequiredService<ITranslationRoomService>();

        // One `now` for both halves. Reading the clock twice would let a room whose grace elapses
        // between the two queries fall through the gap and wait a whole minute for nothing.
        var now = DateTime.UtcNow;

        await OpenDueRoomsAsync(unitOfWork, roomService, now, ct);
        await ExpireMissedRoomsAsync(unitOfWork, roomService, now, ct);
    }

    /// <summary>WT-612: SCHEDULED to OPEN for every booking whose slot has arrived.</summary>
    private async Task OpenDueRoomsAsync(
        IUnitOfWork unitOfWork,
        ITranslationRoomService roomService,
        DateTime now,
        CancellationToken ct)
    {
        // The SQL prefilter and the per-room decision are the same rule, written once in
        // ScheduledOpeningEvaluator: due, not stale, still SCHEDULED. The service re-checks the
        // status itself under its own read, which is what makes a second replica harmless if the
        // lease above ever fails open.
        var due = await unitOfWork.TranslationRoomRepository.FindAsync(
            ScheduledOpeningEvaluator.OpeningSweepCandidateFilter(now),
            string.Empty,
            ct);

        foreach (var room in due)
        {
            if (ct.IsCancellationRequested)
                return;

            var result = await roomService.OpenScheduledRoomAsync(room.Id, ct);
            if (!result.IsSuccess)
            {
                // Expected and harmless in one case: the host started or cancelled the room in the
                // seconds between the query and this call, so it is no longer SCHEDULED. Logged at
                // warning rather than swallowed because the other reasons — a missing room, a
                // failed save — are worth seeing.
                _logger.LogWarning(
                    "Could not open scheduled room {RoomId} due at {ScheduledAt:o}: {Error}",
                    room.Id,
                    room.ScheduledAt,
                    result.Error);
            }
        }
    }

    /// <summary>
    /// WT-714: SCHEDULED or OPEN to EXPIRED for a booking whose grace has run out.
    ///
    /// Run after the opening pass so a booking that is due right now is opened by this same tick
    /// rather than being judged against a status the tick was about to change.
    /// </summary>
    private async Task ExpireMissedRoomsAsync(
        IUnitOfWork unitOfWork,
        ITranslationRoomService roomService,
        DateTime now,
        CancellationToken ct)
    {
        var missed = await unitOfWork.TranslationRoomRepository.FindAsync(
            ScheduledOpeningEvaluator.ExpirySweepCandidateFilter(now),
            string.Empty,
            ct);

        foreach (var room in missed)
        {
            if (ct.IsCancellationRequested)
                return;

            var result = await roomService.ExpireTranslationRoomAsync(room.Id, ct);
            if (!result.IsSuccess)
            {
                // Same shape as the opening half: a host who started or cancelled the room between
                // the query and the call is a refusal that means nothing went wrong, and one room
                // that cannot be expired must not cost every other stale booking this pass.
                _logger.LogWarning(
                    "Could not expire missed booking {RoomId} scheduled for {ScheduledAt:o}: {Error}",
                    room.Id,
                    room.ScheduledAt,
                    result.Error);
            }
        }
    }
}
