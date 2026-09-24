using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// Copies the AI workers' provider-call counters (<c>warptalk:provider_calls:{UTC day}</c>, written
/// by warptalk-ai shared/provider_calls.py) into subscription.provider_call_stats, so the admin
/// Providers page has 90 days of them — Redis keeps each day for 3, Prometheus for 7.
///
/// Every <see cref="ProviderStatusOptions.CallStatsSyncIntervalMinutes"/> (default 2) it reads today
/// and yesterday (yesterday so the last minutes before midnight are not lost) and merges them with
/// the MAX rule of ProviderCallStatMerge: idempotent, and a hash lost to Redis eviction can only
/// undercount. One replica per tick (distributed lease). Nothing escapes the loop: an exception out
/// of ExecuteAsync stops the whole billing host.
/// </summary>
public sealed class ProviderCallStatsSyncWorker : BackgroundService
{
    public const string LockResource = "billing:provider-call-stats-sync";

    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly IDistributedLockProvider _locks;
    private readonly ProviderStatusOptions _options;
    private readonly ILogger<ProviderCallStatsSyncWorker> _logger;
    private readonly TimeProvider _time;

    public ProviderCallStatsSyncWorker(
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        IDistributedLockProvider locks,
        IOptions<ProviderStatusOptions> options,
        ILogger<ProviderCallStatsSyncWorker> logger,
        TimeProvider? time = null)
    {
        _serviceProvider = serviceProvider;
        _redis = redis;
        _locks = locks;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.CallStatsSyncIntervalMinutes <= 0)
        {
            _logger.LogInformation("ProviderCallStatsSyncWorker is disabled (ProviderStatus:CallStatsSyncIntervalMinutes = 0).");
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.CallStatsSyncIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _locks.TryRunExclusiveAsync(LockResource, TimeSpan.FromMinutes(2), SyncAsync, _logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProviderCallStatsSyncWorker: sync failed; retrying next tick.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<int> SyncAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var db = _redis.GetDatabase();
        var rows = new List<Domain.Entities.ProviderCallStat>();
        foreach (var day in new[] { today.AddDays(-1), today })
        {
            var entries = await db.HashGetAllAsync(ProviderCallStatsParser.KeyFor(day));
            if (entries.Length == 0) continue;
            rows.AddRange(ProviderCallStatsParser.Parse(
                day,
                entries.Select(e => new KeyValuePair<string, long>(e.Name.ToString(), long.TryParse(e.Value.ToString(), out var v) ? v : -1))));
        }

        if (rows.Count == 0) return 0;

        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await unitOfWork.ProviderCallStats.MergeAsync(rows, now, ct);
    }
}
