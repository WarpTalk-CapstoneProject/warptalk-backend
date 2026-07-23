using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// Chạy mỗi ngày lúc 00:00 UTC.
/// Với mỗi subscription đang active, ghi lại CreditBalanceSnapshot
/// thể hiện số dư cuối ngày — phục vụ audit, analytics và đối soát.
/// (Plan Mục 8A: Daily Aggregation & Sync)
/// </summary>
public class DailyAuditAggregationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyAuditAggregationWorker> _logger;

    public DailyAuditAggregationWorker(
        IServiceProvider serviceProvider,
        ILogger<DailyAuditAggregationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyAuditAggregationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Tính thời gian chờ đến midnight UTC tiếp theo
            var now = DateTime.UtcNow;
            var nextMidnight = now.Date.AddDays(1);
            var delay = nextMidnight - now;

            _logger.LogInformation(
                "DailyAuditAggregationWorker: next run in {Delay:hh\\:mm\\:ss} at {NextRun:u}.",
                delay, nextMidnight);

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
        var activeSubscriptions = await unitOfWork.SubscriptionRepository
            .Query()
            .Where(s => s.IsActive && s.DeletedAt == null)
            .ToListAsync(cancellationToken);

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
