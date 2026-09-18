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

/// <summary>
/// Copies Cartesia's measured credit usage into subscription.provider_usage_daily, so admin Insights
/// can price dubbing from what Cartesia actually charged instead of an assumed 12.5 characters per
/// second.
///
/// CADENCE
///   Every <see cref="CartesiaUsageOptions.UsageSyncIntervalMinutes"/> (default 10) it re-reads today
///   and the two UTC days before it — Cartesia's finest bucket is a UTC day, so "real time" means
///   today's running total refreshed every few minutes. Once a day (and on start) the same sync
///   reaches back <see cref="CartesiaUsageOptions.UsageBackfillDays"/> (35) days instead. Each sync is
///   three GET /usage/credits calls: ungrouped (the day total, which is also the "this day was
///   synced" marker), by capability and by model.
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

    private readonly IServiceProvider _serviceProvider;
    private readonly ICartesiaUsageClient _client;
    private readonly ICartesiaUsageSyncStatus _status;
    private readonly CartesiaUsageOptions _options;
    private readonly ILogger<CartesiaUsageSyncWorker> _logger;
    private readonly TimeProvider _time;

    private DateTime? _lastBackfillAt;
    private int _failureStreak;
    private string? _loggedProblem;

    public CartesiaUsageSyncWorker(
        IServiceProvider serviceProvider,
        ICartesiaUsageClient client,
        ICartesiaUsageSyncStatus status,
        IOptions<CartesiaUsageOptions> options,
        ILogger<CartesiaUsageSyncWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _serviceProvider = serviceProvider;
        _client = client;
        _status = status;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, _options.UsageSyncIntervalMinutes));

    private TimeSpan MaxBackoff => TimeSpan.FromMinutes(Math.Max(1, _options.MaxBackoffMinutes));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            _status.MarkDisabled(CartesiaUsageSyncStatus.NotConfiguredMessage);
            _logger.LogInformation(
                "CartesiaUsageSyncWorker is disabled: no Cartesia admin API key (Cartesia:AdminApiKey, from CARTESIA_ADMIN_API_KEY). "
                + "Admin Insights will estimate dubbing cost from the rate cards.");
            return;
        }

        if (_options.UsageSyncIntervalMinutes <= 0)
        {
            _status.MarkDisabled("Cartesia usage sync is switched off (Cartesia:UsageSyncIntervalMinutes = 0)");
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
            "CartesiaUsageSyncWorker started; syncing the last {RecentDays} UTC days every {Interval} and {BackfillDays} days every {BackfillHours} h ({Scope}).",
            _options.UsageRecentDays,
            Interval,
            _options.UsageBackfillDays,
            _options.UsageBackfillIntervalHours,
            _options.IsFilteredToApiKey ? "one API key" : "every API key on the account");

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var result = await SyncOnceAsync(stoppingToken);
                delay = AfterSync(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Timeouts (TaskCanceledException), network errors, an unreadable body, the database.
                var problem = ex is OperationCanceledException
                    ? "Cartesia usage sync timed out"
                    : $"Cartesia usage sync failed ({ex.GetType().Name})";
                delay = AfterFailure(problem, retryAfter: null, ex);
            }

            try
            {
                await Task.Delay(delay, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("CartesiaUsageSyncWorker is stopping.");
    }

    /// <summary>
    /// One sync: three reads, then one write of the whole window — or nothing, if any read failed.
    /// Public so a test can drive it without the loop.
    /// </summary>
    public async Task<CartesiaUsageSyncResult> SyncOnceAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var backfill = _lastBackfillAt is null
                       || now - _lastBackfillAt.Value >= TimeSpan.FromHours(Math.Max(1, _options.UsageBackfillIntervalHours));
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

        if (backfill) _lastBackfillAt = now;
        _status.MarkSucceeded(now);
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

    /// <summary>The wait before the next sync, given how this one went. Public for tests.</summary>
    public TimeSpan AfterSync(CartesiaUsageSyncResult result)
    {
        switch (result.Outcome)
        {
            case CartesiaUsageOutcome.Ok:
                if (_failureStreak > 0)
                {
                    _logger.LogInformation(
                        "Cartesia usage sync recovered after {Failures} failed attempt(s); synced {From:yyyy-MM-dd}..{To:yyyy-MM-dd}.",
                        _failureStreak, result.FromDate, result.ToDate);
                }

                _failureStreak = 0;
                _loggedProblem = null;
                return Interval;

            case CartesiaUsageOutcome.Unauthorized:
                // Retrying a rejected key every few minutes changes nothing; check hourly.
                AfterFailure(
                    $"Cartesia rejected the admin API key (HTTP {result.StatusCode}); check the CARTESIA_ADMIN_API_KEY secret",
                    retryAfter: null,
                    exception: null);
                return MaxBackoff;

            case CartesiaUsageOutcome.RateLimited:
                return AfterFailure("Cartesia rate-limited the usage sync (HTTP 429)", result.RetryAfter, exception: null);

            default:
                return AfterFailure(
                    $"Cartesia usage API answered HTTP {result.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}",
                    retryAfter: null,
                    exception: null);
        }
    }

    private TimeSpan AfterFailure(string problem, TimeSpan? retryAfter, Exception? exception)
    {
        _failureStreak++;
        _status.MarkFailed(_time.GetUtcNow().UtcDateTime, problem);

        var backoff = TimeSpan.FromTicks(Math.Min(
            MaxBackoff.Ticks,
            Interval.Ticks * (long)Math.Pow(2, Math.Min(_failureStreak - 1, 10))));
        var delay = retryAfter is { } wait && wait > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, Math.Max(wait.Ticks, TimeSpan.FromMinutes(1).Ticks)))
            : backoff;

        // One line per streak (and when the problem changes), not one per attempt.
        if (!string.Equals(_loggedProblem, problem, StringComparison.Ordinal))
        {
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

        return delay;
    }
}
