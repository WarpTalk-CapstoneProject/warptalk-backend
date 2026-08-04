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
using WarpTalk.BillingService.Domain.Interfaces;
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
        var usageQueue = scope.ServiceProvider.GetRequiredService<IBillingUsageQueue>();
        var aiServiceStateStore = scope.ServiceProvider.GetRequiredService<IAiServiceStateStore>();
        var settlementService = scope.ServiceProvider.GetRequiredService<IUsageSettlementService>();
        var alertService = scope.ServiceProvider.GetService<IBillingOperationalAlertService>();
        var notificationClient = scope.ServiceProvider.GetService<INotificationClient>();
        var subscriptionRepo = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        var rateCardResolver = scope.ServiceProvider.GetRequiredService<IUsageRateCardResolverService>();

        var tempLogsResult = await usageQueue.GetTempUsageLogBatchAsync(_options.BillingAggregationBatchSize, stoppingToken);
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

        var carryOverLogs = await AggregateTempLogsAsync(tempLogs, settlementService, aiServiceStateStore, logger: _logger, alertService, rateCardResolver, notificationClient, subscriptionRepo, stoppingToken);
        var trimResult = await usageQueue.TrimTempUsageLogBatchAsync(tempLogs.Count, stoppingToken);
        if (!trimResult.IsSuccess)
        {
            _logger.LogWarning("Synced aggregated logs to DB, but failed to trim Redis temp usage logs: {Error}", trimResult.Error);
            return;
        }

        if (carryOverLogs != null && carryOverLogs.Count > 0)
        {
            foreach (var log in carryOverLogs)
            {
                await usageQueue.PushTempUsageLogDtoAsync(log, stoppingToken);
            }
            _logger.LogInformation("Successfully pushed {Count} carry-over logs back to Redis.", carryOverLogs.Count);
        }

        _logger.LogInformation($"Successfully synced aggregated logs to DB.");
    }

    public static async Task<IReadOnlyList<TempUsageLogDto>> AggregateTempLogsAsync(
        IReadOnlyList<TempUsageLogDto> tempLogs,
        IUsageSettlementService settlementService,
        IAiServiceStateStore? aiServiceStateStore,
        ILogger? logger,
        IBillingOperationalAlertService? alertService,
        IUsageRateCardResolverService? rateCardResolver,
        INotificationClient? notificationClient,
        ISubscriptionRepository? subscriptionRepo,
        CancellationToken stoppingToken)
    {
        var preProcessedLogs = tempLogs.ToList();
        var carryOverLogs = new List<TempUsageLogDto>();

        // 0. Filter out passthrough and cache hits (0 credit events)
        preProcessedLogs.RemoveAll(log =>
        {
            if (!string.IsNullOrEmpty(log.SourceLanguageCode) && log.SourceLanguageCode == log.TargetLanguageCode)
                return true; // passthrough

            if (!string.IsNullOrWhiteSpace(log.Details) && log.Details.Contains("\"cache_hit\":true", StringComparison.OrdinalIgnoreCase))
                return true; // tts cache hit

            return false;
        });

        // 1. Pre-process AI Assistant tool-call loops:
        // For AI Assistant 'token_in', we only take the MAX quantity/credits per ReferenceId.
        var aiAssistantTokenInGroups = preProcessedLogs
            .Where(x => x.UsageType == UsageConstants.UsageTypes.AiAssistant && x.Unit == "token_in" && x.ReferenceId.HasValue)
            .GroupBy(x => x.ReferenceId!.Value)
            .ToList();

        foreach (var aiGroup in aiAssistantTokenInGroups)
        {
            var maxLog = aiGroup.OrderByDescending(x => x.Quantity).First();
            foreach (var log in aiGroup)
            {
                if (log != maxLog)
                {
                    preProcessedLogs.Remove(log);
                }
            }
        }

        // 1.5 Resolve Rate Cards
        if (rateCardResolver != null)
        {
            foreach (var log in preProcessedLogs)
            {
                if (log.PricingRateCardId == null)
                {
                    var rateResult = await rateCardResolver.ResolveRateCardAsync(
                        log.ChargeType, log.Unit, "VND", log.SourceLanguageCode, log.TargetLanguageCode, stoppingToken);
                    if (rateResult.IsSuccess && rateResult.Value != null)
                    {
                        log.PricingRateCardId = rateResult.Value.Id;
                        log.UnitPriceSnapshot = rateResult.Value.UnitPrice;
                    }
                    else
                    {
                        logger?.LogError("Rate card not found. ChargeType={ChargeType}, Unit={Unit}. Event dropped.", log.ChargeType, log.Unit);
                        log.PricingRateCardId = Guid.Empty; // Mark for removal
                    }
                }
            }
            preProcessedLogs.RemoveAll(x => x.PricingRateCardId == Guid.Empty);
        }

        var groupedLogs = preProcessedLogs
            .GroupBy(l => new
            {
                l.SubscriptionId,
                l.WorkspaceId,
                l.TranslationRoomId,
                l.UsageType,
                l.ChargeType,
                l.Unit,
                l.Provider,
                l.Model,
                l.SourceLanguageCode,
                l.TargetLanguageCode,
                l.PricingScope,
                l.PricingRateCardId,
                l.UnitPriceSnapshot,
                l.ReferenceType
            })
            .ToList();

        foreach (var group in groupedLogs)
        {
            var totalMicroCredits = group.Sum(x => x.MicroCredits ?? (x.CreditsConsumed * UsageConstants.MicroCreditsPerCredit));
            var fullCredits = (int)(totalMicroCredits / UsageConstants.MicroCreditsPerCredit);
            var leftoverMicroCredits = totalMicroCredits % UsageConstants.MicroCreditsPerCredit;

            var totalQuantity = group.Sum(x => x.Quantity);

            if (fullCredits == 0 && totalQuantity == 0)
            {
                if (leftoverMicroCredits > 0)
                {
                    carryOverLogs.Add(new TempUsageLogDto
                    {
                        SubscriptionId = group.Key.SubscriptionId,
                        WorkspaceId = group.Key.WorkspaceId,
                        TranslationRoomId = group.Key.TranslationRoomId,
                        UsageType = group.Key.UsageType,
                        ChargeType = group.Key.ChargeType,
                        Unit = group.Key.Unit,
                        Provider = group.Key.Provider,
                        Model = group.Key.Model,
                        SourceLanguageCode = group.Key.SourceLanguageCode,
                        TargetLanguageCode = group.Key.TargetLanguageCode,
                        PricingScope = group.Key.PricingScope,
                        PricingRateCardId = group.Key.PricingRateCardId,
                        UnitPriceSnapshot = group.Key.UnitPriceSnapshot,
                        ReferenceType = group.Key.ReferenceType,
                        MicroCredits = leftoverMicroCredits,
                        CreditsConsumed = 0,
                        Quantity = 0,
                        IdempotencyKey = $"carryover-{Guid.NewGuid():N}",
                        CreatedAt = DateTime.UtcNow
                    });
                }
                continue;
            }

            if (fullCredits > UsageConstants.MaxCreditsPerFlush)
            {
                logger?.LogError(
                    BillingOperationalEventIds.SettlementFailed,
                    "Bug prevention: Aggregated credits {Credits} exceed max allowed {Max} per flush. SubscriptionId={SubscriptionId}, ChargeType={ChargeType}",
                    fullCredits, UsageConstants.MaxCreditsPerFlush, group.Key.SubscriptionId, group.Key.ChargeType);
                continue;
            }

            if (fullCredits != 0 || totalQuantity != 0)
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

                if (leftoverMicroCredits > 0)
                {
                    carryOverLogs.Add(new TempUsageLogDto
                    {
                        SubscriptionId = group.Key.SubscriptionId,
                        WorkspaceId = group.Key.WorkspaceId,
                        TranslationRoomId = group.Key.TranslationRoomId,
                        UsageType = group.Key.UsageType,
                        ChargeType = group.Key.ChargeType,
                        Unit = group.Key.Unit,
                        Provider = group.Key.Provider,
                        Model = group.Key.Model,
                        SourceLanguageCode = group.Key.SourceLanguageCode,
                        TargetLanguageCode = group.Key.TargetLanguageCode,
                        PricingScope = group.Key.PricingScope,
                        PricingRateCardId = group.Key.PricingRateCardId,
                        UnitPriceSnapshot = group.Key.UnitPriceSnapshot,
                        ReferenceType = group.Key.ReferenceType,
                        MicroCredits = leftoverMicroCredits,
                        CreditsConsumed = 0,
                        Quantity = 0,
                        IdempotencyKey = $"carryover-{Guid.NewGuid():N}",
                        CreatedAt = DateTime.UtcNow
                    });
                }

                var settlementValue = settlement.Value;
                if (settlementValue is null)
                {
                    logger?.LogError(
                        BillingOperationalEventIds.SettlementFailed,
                        "Usage settlement returned success without a value. SubscriptionId={SubscriptionId}, ChargeType={ChargeType}",
                        group.Key.SubscriptionId,
                        group.Key.ChargeType);
                    continue;
                }

                if (settlementValue.ServiceState == SubscriptionConstants.ServiceStates.Suspended)
                {
                    logger?.LogWarning(
                        BillingOperationalEventIds.AiServiceSuspended,
                        "Billing settlement suspended AI service. WorkspaceId={WorkspaceId}, SubscriptionId={SubscriptionId}, Reason={Reason}",
                        group.Key.WorkspaceId,
                        group.Key.SubscriptionId,
                        settlementValue.SuspendedReason);
                }

                if (!string.IsNullOrWhiteSpace(settlementValue.ServiceState) && aiServiceStateStore is not null)
                {
                    await aiServiceStateStore.SetAiServiceStateAsync(
                        group.Key.WorkspaceId,
                        settlementValue.ServiceState,
                        settlementValue.SuspendedReason,
                        stoppingToken);

                    if (settlementRequest.TranslationRoomId.HasValue)
                    {
                        await aiServiceStateStore.SetAiServiceStateForRoomAsync(
                            settlementRequest.TranslationRoomId.Value,
                            settlementValue.ServiceState,
                            settlementValue.SuspendedReason,
                            stoppingToken);
                    }
                }

                if (settlementValue.JustEnteredOverage && notificationClient is not null && subscriptionRepo is not null)
                {
                    try
                    {
                        var sub = await subscriptionRepo.FirstOrDefaultAsync(s => s.Id == group.Key.SubscriptionId, stoppingToken);
                        if (sub is not null)
                        {
                            var msgReq = new SendBillingNotificationsRequest(
                                new[] { sub.UserId },
                                BillingMessageConstants.Notifications.Types.OverageStarted,
                                BillingMessageConstants.Notifications.Titles.OverageStarted,
                                string.Format(BillingMessageConstants.Notifications.Templates.OverageStartedContent, group.Key.WorkspaceId),
                                BillingMessageConstants.Notifications.ActionUrls.Billing,
                                null);
                            await notificationClient.SendNotificationsAsync(msgReq, stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "Failed to send overage notification for WorkspaceId {WorkspaceId}", group.Key.WorkspaceId);
                    }
                }
            }
        }

        return carryOverLogs;
    }
}
