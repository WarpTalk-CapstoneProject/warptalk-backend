using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Application.Mappers;

namespace WarpTalk.BillingService.Infrastructure.Workers;

public class BillingAggregationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BillingAggregationWorker> _logger;

    public BillingAggregationWorker(IServiceProvider serviceProvider, ILogger<BillingAggregationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BillingAggregationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AggregateAndSyncTempLogsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while aggregating billing temp logs.");
            }

            // Run periodically (e.g., every 5 minutes)
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task AggregateAndSyncTempLogsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var redisStore = scope.ServiceProvider.GetRequiredService<IRedisBillingStore>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var tempLogs = (await redisStore.GetAndClearTempUsageLogDtosAsync(stoppingToken)).ToList();

        if (!tempLogs.Any())
        {
            return;
        }

        _logger.LogInformation($"Found {tempLogs.Count} temp usage logs. Aggregating...");

        var groupedLogs = tempLogs
            .GroupBy(l => new { l.SubscriptionId, l.WorkspaceId, l.UsageType, l.ChargeType, l.Unit })
            .ToList();

        foreach (var group in groupedLogs)
        {
            var totalCredits = group.Sum(x => x.CreditsConsumed);
            var totalQuantity = group.Sum(x => x.Quantity);

            if (totalCredits == 0 && totalQuantity == 0)
                continue;

            // Generate aggregated usage record
            var usageRecord = UsageMapper.CreateAggregatedUsageRecord(
                group.Key.SubscriptionId,
                group.Key.WorkspaceId,
                group.Key.UsageType,
                (decimal)totalQuantity,
                group.Key.Unit,
                totalCredits,
                "Aggregated batch"
            );

            await unitOfWork.UsageRecordRepository.AddAsync(usageRecord, stoppingToken);

            // Generate aggregated credit transaction if applicable
            if (totalCredits != 0) 
            {
                var transactionAmount = -totalCredits; 

                var transaction = CreditMapper.CreateAggregatedTransaction(
                    group.Key.SubscriptionId,
                    transactionAmount,
                    group.Key.ChargeType,
                    $"Aggregated {group.Key.ChargeType}"
                );
                transaction.WorkspaceId = group.Key.WorkspaceId;
                transaction.ReferenceType = "AggregatedBatch";
                transaction.CreatedAt = DateTime.UtcNow;

                await unitOfWork.CreditTransactionRepository.AddAsync(transaction, stoppingToken);
            }
        }

        await unitOfWork.SaveChangesAsync(stoppingToken);
        _logger.LogInformation($"Successfully synced aggregated logs to DB.");
    }
}
