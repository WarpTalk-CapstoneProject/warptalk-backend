using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// WT-327: keeps every recurring booking's rolling horizon full.
///
/// Mirrors IdleRoomMonitoringWorker's and ReminderNotificationWorker's shape — a plain polling
/// BackgroundService — deliberately, and NOT a Redis subscriber. Two reasons:
///  1. There is no event to subscribe to. "A day passed" is a clock fact, not a message.
///  2. An unguarded <c>SubscribeAsync</c> takes down the entire host process, not just the
///     worker (see BillingRedisSubscriberService for the guard this service would otherwise
///     need). Not opening that door at all is strictly better than guarding it.
///
/// Redis IS used, for one thing only: a short lease so that N replicas of this service do not
/// all sweep at the same moment. It is an optimisation, not the correctness story — correctness
/// comes from the unique (series_id, series_occurrence_local_date) index, which makes a
/// duplicate materialisation impossible even if every replica sweeps simultaneously. So a Redis
/// outage degrades this to "some wasted work", never to "two rooms for Tuesday".
///
/// Cadence: every 15 minutes. The horizon is 14 days, so nothing is time-critical here; the
/// interval only bounds how long after midnight a newly-in-range day appears on the schedule.
/// </summary>
public class RecurringSeriesMaterializationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RecurringSeriesMaterializationWorker> _logger;

    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Long enough that a slow sweep is not lapped, short enough that a replica killed
    /// mid-sweep does not block the next one for long.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    private const string SweepLockKey = "warptalk:translation-room:series-materialization-lock";

    public RecurringSeriesMaterializationWorker(
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        ILogger<RecurringSeriesMaterializationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RecurringSeriesMaterializationWorker started; horizon {HorizonDays} day(s), sweeping every {Interval}.",
            RecurrenceLimits.HorizonDays, _checkInterval);

        // Sweep once on startup rather than waiting a full interval: a service that has been
        // down over a day boundary owes rooms right now.
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
                // Never rethrow: an unhandled exception out of ExecuteAsync stops the host, and
                // a materialisation hiccup must not take the whole TranslationRoomService with it.
                _logger.LogError(ex, "Error in RecurringSeriesMaterializationWorker.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var leaseToken = Guid.NewGuid().ToString("N");
        var leaseTaken = false;
        IDatabase? database = null;

        try
        {
            database = _redis.GetDatabase();
            leaseTaken = await database.LockTakeAsync(SweepLockKey, leaseToken, LeaseDuration);
            if (!leaseTaken)
            {
                // Another replica is already sweeping. Nothing to do, and nothing wrong.
                return;
            }
        }
        catch (Exception ex)
        {
            // Redis is unavailable. Sweep anyway — the unique index is what actually prevents
            // duplicates, and refusing to materialise because a lease could not be taken would
            // stop meetings being scheduled over a cache outage.
            _logger.LogWarning(ex, "Could not take the series-materialisation lease; sweeping without it.");
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var seriesService = scope.ServiceProvider.GetRequiredService<ITranslationRoomSeriesService>();

            var created = await seriesService.MaterializeDueOccurrencesAsync(ct);
            if (created > 0)
            {
                _logger.LogInformation("RecurringSeriesMaterializationWorker materialised {Count} occurrence(s).", created);
            }
        }
        finally
        {
            if (leaseTaken && database is not null)
            {
                try
                {
                    await database.LockReleaseAsync(SweepLockKey, leaseToken);
                }
                catch (Exception ex)
                {
                    // The lease expires on its own; losing the release is at worst a delayed
                    // next sweep.
                    _logger.LogWarning(ex, "Could not release the series-materialisation lease; it will expire.");
                }
            }
        }
    }
}
