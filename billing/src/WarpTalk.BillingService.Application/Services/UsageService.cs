using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Configuration;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Exceptions;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class UsageService : IUsageService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<UsageService> _logger;
    private readonly BillingRatesOptions _rates;
    private readonly IWorkspaceDirectory? _workspaceDirectory;

    public UsageService(
        IUnitOfWork unitOfWork,
        ILogger<UsageService> logger,
        IOptions<BillingRatesOptions> rates,
        IWorkspaceDirectory? workspaceDirectory = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _rates = rates.Value;
        _workspaceDirectory = workspaceDirectory;
    }

    public int CalculateCreditCost(int audioSeconds, int tokenCount, int gpuInferenceMs, bool isVoiceClone, Plan plan)
    {
        double ratePerMinute = 0;
        if (isVoiceClone)
        {
            ratePerMinute = _rates.VoiceClonePerMinute;
        }
        else
        {
            if (audioSeconds > 0)
            {
                ratePerMinute += _rates.SttPerMinute;
            }
            if (tokenCount > 0)
            {
                ratePerMinute += _rates.TranslationPerMinute;
            }
            if (gpuInferenceMs > 0)
            {
                ratePerMinute += _rates.StandardTtsPerMinute;
            }
        }

        double baseCost = (audioSeconds / 60.0) * ratePerMinute;
        if (baseCost <= 0 && (audioSeconds > 0 || tokenCount > 0 || gpuInferenceMs > 0))
        {
            return 1;
        }

        return (int)Math.Max(1, Math.Ceiling(baseCost));
    }

    public Task<Result<CreditBalanceDto>> RecordUsageAsync(
        RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteWithConcurrencyRetryAsync(request.HostWorkspaceId, async () =>
        {
            if (request.CreditsConsumed <= 0)
                return Result.Failure<CreditBalanceDto>("Credits consumed must be greater than zero.", "INVALID_REQUEST");

            var sub = await GetActiveSubscriptionAsync(request.HostWorkspaceId, true, cancellationToken);

            if (sub is null)
            {
                return Result.Failure<CreditBalanceDto>(
                    "No active subscription found for the host workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);
            }

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan is null)
                return Result.Failure<CreditBalanceDto>("Plan not found.", ErrorCodes.BillingPlanNotFound);

            if (request.UsageType.Contains("voice_clone", StringComparison.OrdinalIgnoreCase))
            {
                if (!plan.VoiceCloneEnabled)
                {
                    return Result.Failure<CreditBalanceDto>(
                        $"Voice clone is not available on the '{plan.Name}' plan. Please upgrade.",
                        "FEATURE_NOT_AVAILABLE");
                }
            }

            if (sub.CreditsRemaining < request.CreditsConsumed)
            {
                return Result.Failure<CreditBalanceDto>(
                    "Insufficient credits in the host workspace.",
                    ErrorCodes.BillingInsufficientCredits);
            }

            sub.CreditsRemaining -= request.CreditsConsumed;
            sub.CreditsUsedThisCycle += request.CreditsConsumed;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            // 1. Create Transaction (Accounting)
            var tx = request.ToCreditTransaction(sub);
            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);

            // 2. Create Usage Record (Analytics)
            var usage = request.ToUsageRecord(sub);
            await _unitOfWork.UsageRecordRepository.AddAsync(usage, cancellationToken);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(sub.ToCreditBalanceDto(request.HostWorkspaceId));
        }, cancellationToken);
    }

    public async Task<Result<bool>> LogUsageOnlyAsync(
        RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.HostWorkspaceId && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<bool>("No active subscription found.", ErrorCodes.BillingSubscriptionNotFound);

            var usage = request.ToUsageRecord(sub);
            await _unitOfWork.UsageRecordRepository.AddAsync(usage, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging usage record for workspace {WorkspaceId}", request.HostWorkspaceId);
            return Result.Failure<bool>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<BillingReportDto>> GetBillingReportAsync(
        Guid workspaceId, int year, int month, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<BillingReportDto>("No active subscription found for this workspace.", ErrorCodes.BillingSubscriptionNotFound);

            var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddMonths(1);

            var txs = await _unitOfWork.CreditTransactionRepository.FindAsync(
                tx => tx.SubscriptionId == sub.Id && tx.CreatedAt >= startDate && tx.CreatedAt < endDate,
                cancellationToken);
            var transactions = txs.OrderBy(tx => tx.CreatedAt).ToList();

            int startingBalance = 0;
            if (transactions.Any())
            {
                var firstTx = transactions.First();
                startingBalance = firstTx.BalanceAfter - firstTx.Amount;
            }
            else
            {
                var priorTxs = await _unitOfWork.CreditTransactionRepository.GetPagedAsync(
                    tx => tx.SubscriptionId == sub.Id && tx.CreatedAt < startDate,
                    0, 1,
                    q => q.OrderByDescending(tx => tx.CreatedAt),
                    cancellationToken);

                var priorTx = priorTxs.FirstOrDefault();
                if (priorTx != null)
                {
                    startingBalance = priorTx.BalanceAfter;
                }
            }

            int endingBalance = transactions.Any() ? transactions.Last().BalanceAfter : startingBalance;

            int totalTopUps = transactions.Where(tx => tx.Type == "top_up").Sum(tx => tx.Amount);
            int totalConsumed = Math.Abs(transactions.Where(tx => tx.Type == "consumption").Sum(tx => tx.Amount));

            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.WorkspaceId == workspaceId && u.RecordedAt >= startDate && u.RecordedAt < endDate,
                cancellationToken);

            var breakdown = usages.GroupBy(u => u.UsageType)
                .Select(g => new UsageBreakdownDto(
                    g.Key,
                    g.Sum(x => x.CreditsConsumed),
                    g.Sum(x => x.Quantity)
                )).ToList();

            var translationUsages = usages.Where(u => u.UsageType.Contains("translation", StringComparison.OrdinalIgnoreCase) && u.Quantity > 0).ToList();
            decimal? averageTranslationCost = translationUsages.Any()
                ? Math.Round(translationUsages.Sum(u => (decimal)u.CreditsConsumed) / translationUsages.Sum(u => u.Quantity), 2)
                : null;

            var meetingGroups = usages.Where(u => u.TranslationRoomId.HasValue)
                                      .GroupBy(u => u.TranslationRoomId!.Value)
                                      .ToList();
            int? averageCostPerMeeting = meetingGroups.Any()
                ? (int)Math.Round(meetingGroups.Average(g => g.Sum(u => u.CreditsConsumed)))
                : null;

            var report = new BillingReportDto(
                workspaceId, month, year, startingBalance, endingBalance,
                totalTopUps, totalConsumed, averageTranslationCost, averageCostPerMeeting, breakdown
            );

            return Result.Success(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating billing report for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<BillingReportDto>("Failed to generate billing report.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<UsageChartDto>> GetWorkspaceUsageChartAsync(Guid workspaceId, int year, CancellationToken cancellationToken = default)
    {
        try
        {
            var subs = await _unitOfWork.SubscriptionRepository.FindAsync(
                s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
                cancellationToken);

            var subIds = subs.Select(s => s.Id).ToList();
            if (!subIds.Any())
                return Result.Failure<UsageChartDto>("No subscription found for this workspace.", ErrorCodes.BillingSubscriptionNotFound);

            var startDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddYears(1);

            var txs = await _unitOfWork.CreditTransactionRepository.FindAsync(
                tx => subIds.Contains(tx.SubscriptionId) && tx.CreatedAt >= startDate && tx.CreatedAt < endDate,
                cancellationToken);

            var monthlyData = Enumerable.Range(1, 12).Select(month =>
            {
                var monthTxs = txs.Where(t => t.CreatedAt.Month == month).ToList();
                var topUp = monthTxs.Where(t => t.Type == "top_up").Sum(t => t.Amount);
                var consumed = Math.Abs(monthTxs.Where(t => t.Type == "consumption").Sum(t => t.Amount));

                return new MonthlyUsageDto(
                    month,
                    new DateTime(year, month, 1).ToString("MMM"),
                    consumed,
                    topUp
                );
            }).ToList();

            return Result.Success(new UsageChartDto(year, monthlyData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage chart for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<UsageChartDto>("Failed to generate chart.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoptionAsync(Guid workspaceId, int days, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-days);

            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.WorkspaceId == workspaceId && u.RecordedAt >= startDate,
                cancellationToken);

            var adoption = usages.GroupBy(u => u.UsageType)
                .Select(g => new FeatureAdoptionDto(
                    g.Key,
                    g.Count(),
                    g.Sum(x => x.CreditsConsumed)
                ))
                .OrderByDescending(x => x.TotalCreditsConsumed)
                .ToList();

            return Result.Success(adoption.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting feature adoption for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<IEnumerable<FeatureAdoptionDto>>("Failed to generate feature adoption.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<GlobalBillingMetricsDto>> GetGlobalMetricsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var subs = await _unitOfWork.SubscriptionRepository.FindAsync(s => s.IsActive && s.DeletedAt == null, cancellationToken);
            var totalBalance = subs.Sum(s => s.CreditsRemaining);
            var activeWorkspaces = subs.Select(s => s.WorkspaceId).Distinct().Count();
            
            var currentMonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(u => u.RecordedAt >= currentMonthStart, cancellationToken);
            var monthlyUsage = usages.Sum(u => u.CreditsConsumed);

            var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
            var auditEvents = await _unitOfWork.CreditTransactionRepository.CountAsync(t => t.CreatedAt >= thirtyDaysAgo, cancellationToken);

            return Result.Success(new GlobalBillingMetricsDto(totalBalance, activeWorkspaces, monthlyUsage, auditEvents));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global metrics");
            return Result.Failure<GlobalBillingMetricsDto>("Failed to generate global metrics.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<UsageChartDto>> GetGlobalUsageChartAsync(int year, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddYears(1);

            var txs = await _unitOfWork.CreditTransactionRepository.FindAsync(
                t => t.CreatedAt >= startDate && t.CreatedAt < endDate,
                cancellationToken);

            var monthlyData = new List<MonthlyUsageDto>();
            for (int i = 1; i <= 12; i++)
            {
                var monthTxs = txs.Where(t => t.CreatedAt.Month == i).ToList();
                var consumed = monthTxs.Where(t => t.Type == "consumption").Sum(t => Math.Abs(t.Amount));
                var topUp = monthTxs.Where(t => t.Type == "top_up" && t.Amount > 0).Sum(t => t.Amount);

                monthlyData.Add(new MonthlyUsageDto(i, new DateTime(year, i, 1).ToString("MMM"), consumed, topUp));
            }

            return Result.Success(new UsageChartDto(year, monthlyData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global usage chart");
            return Result.Failure<UsageChartDto>("Failed to generate global chart.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdownAsync(int days, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-days);

            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.RecordedAt >= startDate,
                cancellationToken);

            var breakdown = usages.GroupBy(u => u.UsageType)
                .Select(g => new UsageSummaryDto(
                    g.Key,
                    g.Sum(x => x.CreditsConsumed)
                ))
                .OrderByDescending(x => x.TotalCreditsConsumed)
                .ToList();

            return Result.Success(breakdown.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global usage breakdown");
            return Result.Failure<IEnumerable<UsageSummaryDto>>("Failed to generate global usage breakdown.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<TopWorkspaceDto>>> GetTopWorkspacesAsync(int days, int limit, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-days);
            
            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.RecordedAt >= startDate,
                cancellationToken);

            var topWorkspaces = usages.GroupBy(u => u.WorkspaceId)
                .Select(g => new TopWorkspaceDto(
                    g.Key,
                    $"Workspace {g.Key.ToString()[..8].ToUpper()}",
                    g.Sum(x => x.CreditsConsumed)
                ))
                .OrderByDescending(x => x.TotalCreditsConsumed)
                .Take(limit)
                .ToList();

            if (topWorkspaces.Any() && _workspaceDirectory is not null)
            {
                try
                {
                    var ids = topWorkspaces.Select(w => w.WorkspaceId).Distinct().ToArray();
                    var workspaceNames = await _workspaceDirectory.GetNamesAsync(
                        ids,
                        cancellationToken);

                    var resolvedTopWorkspaces = new List<TopWorkspaceDto>();
                    foreach (var tw in topWorkspaces)
                    {
                        if (workspaceNames.TryGetValue(tw.WorkspaceId, out var realName))
                        {
                            resolvedTopWorkspaces.Add(tw with { WorkspaceName = realName });
                        }
                        else
                        {
                            resolvedTopWorkspaces.Add(tw);
                        }
                    }
                    topWorkspaces = resolvedTopWorkspaces;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve real workspace names for Top Workspaces");
                }
            }

            return Result.Success(topWorkspaces.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting top workspaces");
            return Result.Failure<IEnumerable<TopWorkspaceDto>>("Failed to generate top workspaces.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<UsageAlertDto>>> GetUsageAlertsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var yesterday = DateTime.UtcNow.AddDays(-1);

            var recentConsumptions = await _unitOfWork.CreditTransactionRepository.FindAsync(
                tx => tx.CreatedAt >= yesterday && tx.Amount < 0,
                cancellationToken);

            var grouped = recentConsumptions
                .GroupBy(tx => tx.SubscriptionId)
                .Select(g => new
                {
                    SubscriptionId = g.Key,
                    ConsumedCredits = Math.Abs(g.Sum(tx => tx.Amount))
                })
                .Where(x => x.ConsumedCredits > 50000)
                .ToList();

            if (!grouped.Any())
                return Result.Success(Enumerable.Empty<UsageAlertDto>());

            var subIds = grouped.Select(g => g.SubscriptionId).Distinct().ToArray();
            var workspaceNames = new Dictionary<Guid, string>();
            var subIdToWorkspaceId = new Dictionary<Guid, Guid>();

            try
            {
                var subscriptions = await _unitOfWork.SubscriptionRepository.FindAsync(
                    subscription => subIds.Contains(subscription.Id),
                    cancellationToken);
                var workspaceIds = new List<Guid>();
                foreach (var subscription in subscriptions)
                {
                    subIdToWorkspaceId[subscription.Id] = subscription.WorkspaceId;
                    workspaceIds.Add(subscription.WorkspaceId);
                }

                if (workspaceIds.Any() && _workspaceDirectory is not null)
                    workspaceNames = new Dictionary<Guid, string>(
                        await _workspaceDirectory.GetNamesAsync(workspaceIds, cancellationToken));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve workspace names for alerts");
            }

            var alerts = grouped.Select(g => {
                var wId = subIdToWorkspaceId.TryGetValue(g.SubscriptionId, out var id) ? id : Guid.Empty;
                return new UsageAlertDto(
                    WorkspaceId: wId,
                    WorkspaceName: workspaceNames.TryGetValue(wId, out var name) ? name : "Unknown Workspace",
                    ConsumedCreditsIn24h: g.ConsumedCredits,
                    Reason: $"Unusually high consumption: {g.ConsumedCredits} credits in 24h"
                );
            });

            return Result.Success(alerts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage alerts");
            return Result.Failure<IEnumerable<UsageAlertDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public Result<ServiceRatesDto> GetServiceRates()
    {
        var dto = new ServiceRatesDto(
            SttPerMinute: _rates.SttPerMinute,
            TranslationPerMinute: _rates.TranslationPerMinute,
            StandardTtsPerMinute: _rates.StandardTtsPerMinute,
            VoiceClonePerMinute: _rates.VoiceClonePerMinute,
            AiSummaryPerRequest: _rates.AiSummaryPerRequest,
            AiChatPerRequest: _rates.AiChatPerRequest
        );
        return Result.Success(dto);
    }

    private async Task<WarpTalk.BillingService.Domain.Entities.Subscription?> GetActiveSubscriptionAsync(
        Guid workspaceId, bool requireActivePeriod = false, CancellationToken cancellationToken = default)
    {
        return await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null &&
                 (!requireActivePeriod || s.CurrentPeriodEnd >= DateTime.UtcNow),
            cancellationToken);
    }

    private async Task<Result<T>> ExecuteWithConcurrencyRetryAsync<T>(
        Guid workspaceId,
        Func<Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (ConcurrencyConflictException ex)
            {
                _logger.LogWarning(ex, "Concurrency conflict for WorkspaceId {WorkspaceId}. Attempt {Attempt} of {MaxRetries}", workspaceId, attempt, maxRetries);
                if (attempt == maxRetries) return Result.Failure<T>("System is busy. Please try again later.", "CONCURRENCY_ERROR");

                await Task.Delay(50 * attempt, cancellationToken);
                _unitOfWork.ClearTracking();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing operation for WorkspaceId {WorkspaceId}", workspaceId);
                return Result.Failure<T>("An unexpected error occurred.", "INTERNAL_ERROR");
            }
        }
        return Result.Failure<T>("System is busy. Please try again later.", "CONCURRENCY_ERROR");
    }

    private async Task<int> GetVoiceCloneMinutesUsedThisCycleAsync(Guid subscriptionId, DateTime periodStart, DateTime periodEnd, CancellationToken cancellationToken)
    {
        var voiceCloneUsages = await _unitOfWork.UsageRecordRepository.FindAsync(
            u => u.SubscriptionId == subscriptionId &&
                 u.UsageType.Contains("voice_clone", StringComparison.OrdinalIgnoreCase) &&
                 u.RecordedAt >= periodStart &&
                 u.RecordedAt < periodEnd,
            cancellationToken);

        var totalSeconds = voiceCloneUsages.Sum(u => u.DurationSeconds ?? 0);
        return (int)Math.Ceiling(totalSeconds / 60.0);
    }
}
