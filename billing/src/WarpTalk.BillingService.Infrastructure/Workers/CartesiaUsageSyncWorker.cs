using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Services;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>What one sync did.</summary>
public sealed record CartesiaUsageSyncResult(
    CartesiaUsageOutcome Outcome,
    DateOnly FromDate,
    DateOnly ToDate,
    int Rows,
    bool Backfill,
    int? StatusCode = null,
    TimeSpan? RetryAfter = null);

/// <summary>What one pass of the loop did: the sync (null when another replica had the turn) and the wait.</summary>
public sealed record CartesiaSyncTurnOutcome(CartesiaUsageSyncResult? Result, TimeSpan NextSyncIn);

/// <summary>
/// Copies Cartesia's measured credit usage into subscription.provider_usage_daily, so admin Insights
/// can price dubbing from what Cartesia actually charged instead of an assumed 12.5 characters per
/// second.
///
/// CADENCE
///   Every <see cref="CartesiaUsageOptions.UsageSyncIntervalMinutes"/> (default 10) it re-reads today
///   and the two UTC days before it — Cartesia's finest bucket is a UTC day, so "real time" means
///   today's running total refreshed every few minutes. Once a day the same sync reaches back
///   <see cref="CartesiaUsageOptions.UsageBackfillDays"/> (35) days instead. Each sync is three
///   GET /usage/credits calls: ungrouped (the day total, which is also the "this day was synced"
///   marker), by capability and by model.
///
/// REPLICAS
///   Billing runs several replicas. They share one schedule through <see cref="ICartesiaSyncCoordinator"/>
///   (a Redis lease): each polls for the turn every minute, only the holder calls Cartesia, and it holds
///   the lease until the next sync is due — so the account sees one sync per interval, not one per
///   replica, and a backoff holds for all of them. Status and failure streak are shared the same way.
///
/// FAILURE
///   No admin key → disabled, logged once, and Insights keep estimating. 401/403, 429 and other
///   failures back off (Retry-After when Cartesia sends one, else doubling up to
///   <see cref="CartesiaUsageOptions.MaxBackoffMinutes"/>) and log ONE line per failure streak, not one
///   per attempt. Nothing is written from a partial sync. Every exception is caught inside the loop:
///   an exception escaping ExecuteAsync stops the whole billing host (BackgroundServiceExceptionBehavior
///   .StopHost), not just this worker. The key is never logged — it only ever sits in the
///   Authorization header of the outgoing request.
/// </summary>
public sealed class CartesiaUsageSyncWorker : BackgroundService
{
    private static readonly string[] SyncedKinds =
    {
        ProviderUsageConstants.GroupKinds.Total,
        ProviderUsageConstants.GroupKinds.Capability,
        ProviderUsageConstants.GroupKinds.Model,
    };

    /// <summary>How often a replica without the turn asks for it. One Redis SET NX; cheap.</summary>
    private static readonly TimeSpan TurnPoll = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest one sync may run: the turn lease minus a margin for clock drift, the write and
    /// the lease extension. Past it the sync is cancelled, so it can never still be calling
    /// Cartesia when another replica is allowed to take the turn.
    /// </summary>
    public static readonly TimeSpan SyncBudget = RedisCartesiaSyncCoordinator.TurnTimeout - TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly ICartesiaUsageClient _client;
    private readonly ICartesiaUsageSyncStatus _status;
    private readonly ICartesiaSyncCoordinator _coordinator;
    private readonly CartesiaUsageOptions _options;
    private readonly ILogger<CartesiaUsageSyncWorker> _logger;
    private readonly TimeProvider _time;

    private string? _loggedProblem;

    public CartesiaUsageSyncWorker(
        IServiceProvider serviceProvider,
        ICartesiaUsageClient client,
        ICartesiaUsageSyncStatus status,
        ICartesiaSyncCoordinator coordinator,
        IOptions<CartesiaUsageOptions> options,
        ILogger<CartesiaUsageSyncWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _serviceProvider = serviceProvider;
        _client = client;
        _status = status;
        _coordinator = coordinator;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, _options.UsageSyncIntervalMinutes));

    private TimeSpan MaxBackoff => TimeSpan.FromMinutes(Math.Max(1, _options.MaxBackoffMinutes));

    private TimeSpan BackfillEvery => TimeSpan.FromHours(Math.Max(1, _options.UsageBackfillIntervalHours));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            await _status.MarkDisabledAsync(CartesiaUsageSyncStatus.NotConfiguredMessage, stoppingToken);
            _logger.LogInformation(
                "CartesiaUsageSyncWorker is disabled: no Cartesia admin API key (Cartesia:AdminApiKey, from CARTESIA_ADMIN_API_KEY). "
                + "Admin Insights will estimate dubbing cost from the rate cards.");
            return;
        }

        if (_options.UsageSyncIntervalMinutes <= 0)
        {
            await _status.MarkDisabledAsync("Cartesia usage sync is switched off (Cartesia:UsageSyncIntervalMinutes = 0)", stoppingToken);
            _logger.LogInformation(
                "CartesiaUsageSyncWorker is disabled (Cartesia:UsageSyncIntervalMinutes = {Interval}).",
                _options.UsageSyncIntervalMinutes);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.UsageApiKeyId) && !_options.IsFilteredToApiKey)
        {
            _logger.LogWarning(
                "Cartesia:UsageApiKeyId is not a UUID and is ignored; usage of every API key on the Cartesia account is synced.");
        }

        _logger.LogInformation(
            "CartesiaUsageSyncWorker started; syncing the last {RecentDays} UTC days every {Interval} and {BackfillDays} days every {BackfillHours} h ({Scope}), one replica at a time.",
            _options.UsageRecentDays,
            Interval,
            _options.UsageBackfillDays,
            _options.UsageBackfillIntervalHours,
            _options.IsFilteredToApiKey ? "one API key" : "every API key on the account");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTurnAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // RunTurnAsync already guards the sync itself; this is the coordinator or the status
                // store failing (Redis). Skip the turn rather than sync uncoordinated.
                LogOnce($"Cartesia usage sync could not coordinate with other replicas ({ex.GetType().Name})", TurnPoll, ex);
            }

            try
            {
                await Task.Delay(TurnPoll, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("CartesiaUsageSyncWorker is stopping.");
    }

    /// <summary>
    /// One pass: take the turn if it is ours and due, sync, then hold the turn until the next sync is
    /// due (the interval, or the backoff after a failure). Public so a test can drive it without the loop.
    /// </summary>
    public async Task<CartesiaSyncTurnOutcome> RunTurnAsync(CancellationToken ct)
    {
        var turn = await _coordinator.TryBeginTurnAsync(BackfillEvery, ct);
        if (turn is null) return new CartesiaSyncTurnOutcome(null, TurnPoll);

        CartesiaUsageSyncResult result;
        TimeSpan next;

        // The turn lease is taken for TurnTimeout and NOT renewed while the sync runs, so a sync
        // that outlived it would overlap with the next replica's turn — two replicas calling
        // Cartesia's admin API and replacing the same snapshot window at once. Three requests at
        // the configurable per-request timeout (up to 120 s each) can exceed the 5-minute lease,
        // so the sync is cut off a safety margin before the lease can expire. A cut-off sync
        // writes nothing (the write is one SaveChanges) and takes the ordinary failure path.
        using var runBudget = new CancellationTokenSource(SyncBudget, _time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, runBudget.Token);
        try
        {
            result = await SyncOnceAsync(turn.Backfill, bounded.Token);
            next = await AfterSyncAsync(result, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Timeouts (TaskCanceledException), network errors, an unreadable body, the database.
            var problem = ex is OperationCanceledException
                ? "Cartesia usage sync timed out"
                : $"Cartesia usage sync failed ({ex.GetType().Name})";
            var now = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
            result = new CartesiaUsageSyncResult(CartesiaUsageOutcome.Failed, now, now, 0, turn.Backfill);
            next = await AfterFailureAsync(problem, retryAfter: null, ex, ct);
        }

        await _coordinator.EndTurnAsync(
            turn, next, backfilled: turn.Backfill && result.Outcome == CartesiaUsageOutcome.Ok, BackfillEvery, ct);
        return new CartesiaSyncTurnOutcome(result, next);
    }

    /// <summary>One sync: three reads, then one write of the whole window — or nothing, if any read failed.</summary>
    public async Task<CartesiaUsageSyncResult> SyncOnceAsync(bool backfill, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var days = Math.Clamp(backfill ? _options.UsageBackfillDays : _options.UsageRecentDays, 1, 365);
        var from = today.AddDays(-(days - 1));

        var totals = await _client.GetCreditsAsync(from, today, null, ct);
        if (totals.Outcome != CartesiaUsageOutcome.Ok) return Failed(totals);
        var capabilities = await _client.GetCreditsAsync(from, today, ProviderUsageConstants.GroupKinds.Capability, ct);
        if (capabilities.Outcome != CartesiaUsageOutcome.Ok) return Failed(capabilities);
        var models = await _client.GetCreditsAsync(from, today, ProviderUsageConstants.GroupKinds.Model, ct);
        if (models.Outcome != CartesiaUsageOutcome.Ok) return Failed(models);

        var rows = BuildRows(from, today, totals.Series, capabilities.Series, models.Series);

        using (var scope = _serviceProvider.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ProviderUsageDaily.ReplaceWindowAsync(
                ProviderUsageConstants.Providers.Cartesia, from, today, SyncedKinds, rows, now, ct);
        }

        return new CartesiaUsageSyncResult(CartesiaUsageOutcome.Ok, from, today, rows.Count, backfill, totals.StatusCode);

        CartesiaUsageSyncResult Failed(CartesiaCreditUsage usage)
            => new(usage.Outcome, from, today, 0, backfill, usage.StatusCode, usage.RetryAfter);
    }

    /// <summary>
    /// Rows for provider_usage_daily: a <c>total</c> row for EVERY day of the window — 0 when Cartesia
    /// reported nothing, which still proves the day was synced — and a capability / model row for each
    /// group with credits that day.
    /// </summary>
    public static IReadOnlyList<ProviderUsageDaily> BuildRows(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<CartesiaCreditSeries> totals,
        IReadOnlyList<CartesiaCreditSeries> capabilities,
        IReadOnlyList<CartesiaCreditSeries> models)
    {
        var rows = new List<ProviderUsageDaily>();

        var byDay = totals
            .SelectMany(series => series.Buckets)
            .GroupBy(bucket => DateOnly.FromDateTime(bucket.StartTs))
            .ToDictionary(group => group.Key, group => group.Sum(bucket => bucket.Credits));
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            rows.Add(Row(day, ProviderUsageConstants.GroupKinds.Total, ProviderUsageConstants.TotalGroupId, null,
                byDay.GetValueOrDefault(day)));
        }

        AddGroups(ProviderUsageConstants.GroupKinds.Capability, capabilities);
        AddGroups(ProviderUsageConstants.GroupKinds.Model, models);
        return rows;

        void AddGroups(string kind, IReadOnlyList<CartesiaCreditSeries> series)
        {
            foreach (var group in series)
            {
                var id = Truncate(group.Id.Trim(), 200);
                var label = group.Label is null ? null : Truncate(group.Label, 300);
                foreach (var day in group.Buckets
                             .GroupBy(bucket => DateOnly.FromDateTime(bucket.StartTs))
                             .Where(day => day.Key >= from && day.Key <= to))
                {
                    var credits = day.Sum(bucket => bucket.Credits);
                    if (credits > 0) rows.Add(Row(day.Key, kind, id, label, credits));
                }
            }
        }

        static ProviderUsageDaily Row(DateOnly day, string kind, string id, string? label, long credits) => new()
        {
            Id = Guid.NewGuid(),
            Provider = ProviderUsageConstants.Providers.Cartesia,
            UsageDate = day,
            GroupKind = kind,
            GroupId = id,
            GroupLabel = label,
            Credits = credits,
        };

        static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
    }

    /// <summary>The wait before the next sync, given how this one went; records the outcome in the shared status.</summary>
    private async Task<TimeSpan> AfterSyncAsync(CartesiaUsageSyncResult result, CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case CartesiaUsageOutcome.Ok:
                var before = await _status.GetAsync(ct);
                await _status.MarkSucceededAsync(_time.GetUtcNow().UtcDateTime, ct);
                if (before.FailureStreak > 0)
                {
                    _logger.LogInformation(
                        "Cartesia usage sync recovered after {Failures} failed attempt(s); synced {From:yyyy-MM-dd}..{To:yyyy-MM-dd}.",
                        before.FailureStreak, result.FromDate, result.ToDate);
                }

                _loggedProblem = null;
                return Interval;

            case CartesiaUsageOutcome.Unauthorized:
                // Retrying a rejected key every few minutes changes nothing; check hourly.
                await AfterFailureAsync(
                    $"Cartesia rejected the admin API key (HTTP {result.StatusCode}); check the CARTESIA_ADMIN_API_KEY secret",
                    retryAfter: null,
                    exception: null,
                    ct);
                return MaxBackoff;

            case CartesiaUsageOutcome.RateLimited:
                return await AfterFailureAsync("Cartesia rate-limited the usage sync (HTTP 429)", result.RetryAfter, exception: null, ct);

            default:
                return await AfterFailureAsync(
                    $"Cartesia usage API answered HTTP {result.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}",
                    retryAfter: null,
                    exception: null,
                    ct);
        }
    }

    private async Task<TimeSpan> AfterFailureAsync(string problem, TimeSpan? retryAfter, Exception? exception, CancellationToken ct)
    {
        // The streak is shared: a replica taking over mid-streak keeps backing off instead of restarting.
        var state = await _status.MarkFailedAsync(_time.GetUtcNow().UtcDateTime, problem, ct);
        var streak = Math.Max(1, state.FailureStreak);

        var backoff = TimeSpan.FromTicks(Math.Min(
            MaxBackoff.Ticks,
            Interval.Ticks * (long)Math.Pow(2, Math.Min(streak - 1, 10))));
        var delay = retryAfter is { } wait && wait > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, Math.Max(wait.Ticks, TimeSpan.FromMinutes(1).Ticks)))
            : backoff;

        LogOnce(problem, delay, exception);
        return delay;
    }

    /// <summary>One line per streak (and when the problem changes), not one per attempt.</summary>
    private void LogOnce(string problem, TimeSpan delay, Exception? exception)
    {
        if (string.Equals(_loggedProblem, problem, StringComparison.Ordinal)) return;

        _loggedProblem = problem;
        if (exception is null)
        {
            _logger.LogWarning("{Problem}. Next attempt in {Delay}; Insights estimate dubbing cost until it succeeds.", problem, delay);
        }
        else
        {
            _logger.LogWarning(exception, "{Problem}. Next attempt in {Delay}; Insights estimate dubbing cost until it succeeds.", problem, delay);
        }
    }
}
