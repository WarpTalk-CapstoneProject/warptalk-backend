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
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Logging;
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
        var settlementService = scope.ServiceProvider.GetRequiredService<IUsageSettlementService>();
        var alertService = scope.ServiceProvider.GetService<IBillingOperationalAlertService>();

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
        _logger.LogInformation($"Found {tempLogs.Count} temp usage logs. Aggregating...");

        await AggregateTempLogsAsync(tempLogs, settlementService, redisStore, _logger, alertService, stoppingToken);
        var trimResult = await redisStore.TrimTempUsageLogBatchAsync(tempLogs.Count, stoppingToken);
        if (!trimResult.IsSuccess)
        {
            _logger.LogWarning("Synced aggregated logs to DB, but failed to trim Redis temp usage logs: {Error}", trimResult.Error);
            return;
        }

        _logger.LogInformation($"Successfully synced aggregated logs to DB.");
    }

    public static async Task AggregateTempLogsAsync(
        IReadOnlyList<TempUsageLogDto> tempLogs,
        IUsageSettlementService settlementService,
        IRedisBillingStore? redisStore,
        ILogger? logger,
        IBillingOperationalAlertService? alertService,
        CancellationToken stoppingToken)
    {
        var groupedLogs = tempLogs
            .GroupBy(l => new
            {
                l.SubscriptionId,
                l.WorkspaceId,
                l.UsageType,
                l.ChargeType,
                l.Unit,
                l.Provider,
                l.Model,
                l.PricingRateCardId,
                l.UnitPriceSnapshot,
                l.ReferenceType
            })
            .ToList();

        foreach (var group in groupedLogs)
        {
            var totalCredits = group.Sum(x => x.CreditsConsumed);
            var totalQuantity = group.Sum(x => x.Quantity);

            if (totalCredits == 0 && totalQuantity == 0)
                continue;

            if (totalCredits != 0)
            {
                var settlementRequest = group.ToAggregatedSettlementRequest();
                var settlement = await settlementService.SettleUsageChargeAsync(
                    settlementRequest,
                    stoppingToken);

                if (!settlement.IsSuccess)
                {
                    logger?.LogError(
                        BillingOperationalEventIds.SettlementFailed,
                        "Failed to settle aggregated billing group. SubscriptionId={SubscriptionId}, ChargeType={ChargeType}, Error={Error}",
                        group.Key.SubscriptionId,
                        group.Key.ChargeType,
                        settlement.Error);
                    if (alertService is not null)
                        await alertService.AlertSettlementFailedAsync(settlementRequest, settlement.Error, stoppingToken);

                    continue;
                }

                if (settlement.Value?.ServiceState == SubscriptionConstants.ServiceStates.Suspended)
                {
                    logger?.LogWarning(
                        BillingOperationalEventIds.AiServiceSuspended,
                        "Billing settlement suspended AI service. WorkspaceId={WorkspaceId}, SubscriptionId={SubscriptionId}, Reason={Reason}",
                        group.Key.WorkspaceId,
                        group.Key.SubscriptionId,
                        settlement.Value.SuspendedReason);
                }

                if (!string.IsNullOrWhiteSpace(settlement.Value?.ServiceState) && redisStore is not null)
                {
                    await redisStore.SetAiServiceStateAsync(
                        group.Key.WorkspaceId,
                        settlement.Value.ServiceState!,
                        settlement.Value.SuspendedReason,
                        stoppingToken);

                    if (settlementRequest.TranslationRoomId.HasValue)
                    {
                        await redisStore.SetAiServiceStateForRoomAsync(
                            settlementRequest.TranslationRoomId.Value,
                            settlement.Value.ServiceState!,
                            settlement.Value.SuspendedReason,
                            stoppingToken);
                    }
                }
            }
        }
    }

}
