using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;

namespace WarpTalk.BillingService.Infrastructure.Workers;

public class DailyAuditAggregationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyAuditAggregationWorker> _logger;
    private readonly BillingWorkerOptions _options;

    public DailyAuditAggregationWorker(
        IServiceProvider serviceProvider,
        ILogger<DailyAuditAggregationWorker> logger,
        IOptions<BillingWorkerOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyAuditAggregationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Tính thời gian chờ đến midnight UTC tiếp theo
            var now = DateTime.UtcNow;
            var nextRun = now.Date.AddHours(_options.DailyAuditHourUtc);
            if (nextRun <= now)
                nextRun = nextRun.AddDays(1);
            var delay = nextRun - now;

            _logger.LogInformation(
                "DailyAuditAggregationWorker: next run in {Delay:hh\\:mm\\:ss} at {NextRun:u}.",
                delay, nextRun);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await AggregateAndSnapshotAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DailyAuditAggregationWorker: error during daily aggregation.");
            }
        }

        _logger.LogInformation("DailyAuditAggregationWorker is stopping.");
    }

    private async Task AggregateAndSnapshotAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var snapshotAt = DateTime.UtcNow;

        // Lấy tất cả subscriptions đang active (chưa xóa mềm)
        var activeSubscriptions = await unitOfWork.SubscriptionRepository.GetActiveSubscriptionsAsync(cancellationToken);

        if (activeSubscriptions.Count == 0)
        {
            _logger.LogInformation("DailyAuditAggregationWorker: no active subscriptions to snapshot.");
            return;
        }

        var snapshots = activeSubscriptions.Select(sub => new CreditBalanceSnapshot
        {
            Id = Guid.NewGuid(),
            SubscriptionId = sub.Id,
            CreditsRemaining = sub.CreditsRemaining,
            CreditsUsedThisCycle = sub.CreditsUsedThisCycle,
            SnapshotAt = snapshotAt
        }).ToList();

        foreach (var snapshot in snapshots)
            await unitOfWork.CreditBalanceSnapshotRepository.AddAsync(snapshot, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "DailyAuditAggregationWorker: snapshotted {Count} active subscriptions at {SnapshotAt:u}.",
            snapshots.Count, snapshotAt);
    }
}
