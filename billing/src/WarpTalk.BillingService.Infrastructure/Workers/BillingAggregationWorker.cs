using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Infrastructure.Options;

namespace WarpTalk.BillingService.Infrastructure.Workers;

public class BillingAggregationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BillingAggregationWorker> _logger;
    private readonly BillingWorkerOptions _options;

    public BillingAggregationWorker(
        IServiceProvider serviceProvider,
        ILogger<BillingAggregationWorker> logger,
        IOptions<BillingWorkerOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
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

            await Task.Delay(_options.BillingAggregationInterval, stoppingToken);
        }
    }

    private async Task AggregateAndSyncTempLogsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var redisStore = scope.ServiceProvider.GetRequiredService<IRedisBillingStore>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var tempLogsResult = await redisStore.GetTempUsageLogBatchAsync(_options.BillingAggregationBatchSize, stoppingToken);
        if (!tempLogsResult.IsSuccess)
        {
            _logger.LogWarning("Failed to get temp usage logs from Redis: {Error}", tempLogsResult.Error);
            return;
        }

        var tempLogs = (tempLogsResult.Value ?? Array.Empty<TempUsageLogDto>()).ToList();

        if (!tempLogs.Any())
        {
            return;
        }
        //found num logs and aggregat
        _logger.LogInformation($"Found {tempLogs.Count} temp usage logs. Aggregating...");

        var groupedLogs = tempLogs
            .GroupBy(l => new { l.SubscriptionId, l.WorkspaceId, l.UsageType, l.ChargeType, l.Unit })
            .ToList();
        //Group logs by subscription id, workspace id, usage type, charge type, and unit
        foreach (var group in groupedLogs)
        {
            var totalCredits = group.Sum(x => x.CreditsConsumed);
            var totalQuantity = group.Sum(x => x.Quantity);

            if (totalCredits == 0 && totalQuantity == 0)
                continue;

            // Generate aggregated usage record
            var usageRecord = UsageMapper.CreateAggregatedUsageRecord(new CreateAggregatedUsageRecordRequest(
                SubscriptionId: group.Key.SubscriptionId,
                WorkspaceId: group.Key.WorkspaceId,
                UsageType: group.Key.UsageType,
                Quantity: (decimal)totalQuantity,
                Unit: group.Key.Unit,
                CreditsConsumed: totalCredits,
                Details: BillingMessageConstants.UsageMessages.AggregatedBatchDescription));

            await unitOfWork.UsageRecordRepository.AddAsync(usageRecord, stoppingToken);

            // Generate aggregated credit transaction if applicable
            if (totalCredits != 0) 
            {
                var transactionAmount = -totalCredits; 

                var transaction = CreditMapper.CreateAggregatedTransaction(
                    group.Key.SubscriptionId,
                    transactionAmount,
                    group.Key.ChargeType,
                    string.Format(BillingMessageConstants.UsageMessages.AggregatedChargeDescriptionTemplate, group.Key.ChargeType)
                );
                transaction.WorkspaceId = group.Key.WorkspaceId;
                transaction.ReferenceType = TransactionConstants.ReferenceTypes.AggregatedBatch;
                transaction.CreatedAt = DateTime.UtcNow;

                await unitOfWork.CreditTransactionRepository.AddAsync(transaction, stoppingToken);
            }
        }

        await unitOfWork.SaveChangesAsync(stoppingToken);
        var trimResult = await redisStore.TrimTempUsageLogBatchAsync(tempLogs.Count, stoppingToken);
        if (!trimResult.IsSuccess)
        {
            _logger.LogWarning("Synced aggregated logs to DB, but failed to trim Redis temp usage logs: {Error}", trimResult.Error);
            return;
        }

        _logger.LogInformation($"Successfully synced aggregated logs to DB.");
    }
}
