using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class BillingAnalyticsService : IBillingAnalyticsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<BillingAnalyticsService> _logger;

    public BillingAnalyticsService(
        IUnitOfWork unitOfWork,
        ILogger<BillingAnalyticsService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<BillingReportDto>> GetBillingReportAsync(Guid workspaceId, BillingReportQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<BillingReportDto>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);

            var startDate = new DateTime(query.Year, query.Month, 1, 0, 0, 0, DateTimeKind.Utc);
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
                var priorTx = await _unitOfWork.CreditTransactionRepository.GetLatestBeforeAsync(sub.Id, startDate, cancellationToken);
                if (priorTx != null)
                {
                    startingBalance = priorTx.BalanceAfter;
                }
            }

            int endingBalance = transactions.Any() ? transactions.Last().BalanceAfter : startingBalance;

            int totalTopUps = transactions.Where(tx => tx.Type == TransactionConstants.TransactionTypes.TopUp).Sum(tx => tx.Amount);
            int totalConsumed = Math.Abs(transactions.Where(tx => tx.Type == TransactionConstants.TransactionTypes.Consume).Sum(tx => tx.Amount));

            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.WorkspaceId == workspaceId && u.RecordedAt >= startDate && u.RecordedAt < endDate,
                cancellationToken);

            var breakdown = usages.GroupBy(u => u.UsageType)
                .Select(BillingAnalyticsMapper.ToUsageBreakdownDto)
                .ToList();

            var translationUsages = usages.Where(u => u.UsageType.Contains(UsageConstants.UsageTypes.TranslationKeyword, StringComparison.OrdinalIgnoreCase) && u.Quantity > 0).ToList();
            decimal? averageTranslationCost = translationUsages.Any()
                ? Math.Round(translationUsages.Sum(u => (decimal)u.CreditsConsumed) / translationUsages.Sum(u => u.Quantity), 2)
                : null;

            var meetingGroups = usages.Where(u => u.TranslationRoomId.HasValue)
                                      .GroupBy(u => u.TranslationRoomId!.Value)
                                      .ToList();
            int? averageCostPerMeeting = meetingGroups.Any()
                ? (int)Math.Round(meetingGroups.Average(g => g.Sum(u => u.CreditsConsumed)))
                : null;

            var report = BillingAnalyticsMapper.ToBillingReportDto(
                workspaceId, query, startingBalance, endingBalance,
                totalTopUps, totalConsumed, averageTranslationCost, averageCostPerMeeting, breakdown
            );

            return Result.Success(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGeneratingBillingReport, workspaceId);
            return Result.Failure<BillingReportDto>(BillingMessageConstants.ApiErrorMessages.BillingAnalyticsReportFailed, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<UsageChartDto>> GetWorkspaceUsageChartAsync(Guid workspaceId, UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var subs = await _unitOfWork.SubscriptionRepository.FindAsync(
                s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
                cancellationToken);

            var subIds = subs.Select(s => s.Id).ToList();
            if (!subIds.Any())
                return Result.Failure<UsageChartDto>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);

            var startDate = new DateTime(query.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddYears(1);

            var txs = await _unitOfWork.CreditTransactionRepository.FindAsync(
                tx => subIds.Contains(tx.SubscriptionId) && tx.CreatedAt >= startDate && tx.CreatedAt < endDate,
                cancellationToken);

            var monthlyData = Enumerable.Range(1, 12).Select(month =>
                BillingAnalyticsMapper.ToMonthlyUsageDto(query.Year, month, txs)).ToList();

            return Result.Success(BillingAnalyticsMapper.ToUsageChartDto(query.Year, monthlyData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingUsageChart, workspaceId);
            return Result.Failure<UsageChartDto>(BillingMessageConstants.ApiErrorMessages.BillingAnalyticsChartFailed, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoptionAsync(Guid workspaceId, UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-query.Days);

            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.WorkspaceId == workspaceId && u.RecordedAt >= startDate,
                cancellationToken);

            var adoption = usages.GroupBy(u => u.UsageType)
                .Select(BillingAnalyticsMapper.ToFeatureAdoptionDto)
                .OrderByDescending(x => x.TotalCreditsConsumed)
                .ToList();

            return Result.Success(adoption.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingFeatureAdoption, workspaceId);
            return Result.Failure<IEnumerable<FeatureAdoptionDto>>(BillingMessageConstants.ApiErrorMessages.BillingAnalyticsAdoptionFailed, ErrorCodes.InternalServerError);
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

            return Result.Success(BillingAnalyticsMapper.ToGlobalBillingMetricsDto(totalBalance, activeWorkspaces, monthlyUsage, auditEvents));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingGlobalMetrics);
            return Result.Failure<GlobalBillingMetricsDto>(BillingMessageConstants.ApiErrorMessages.BillingAnalyticsGlobalMetricsFailed, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<UsageChartDto>> GetGlobalUsageChartAsync(UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = new DateTime(query.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddYears(1);

            var txs = await _unitOfWork.CreditTransactionRepository.FindAsync(
                t => t.CreatedAt >= startDate && t.CreatedAt < endDate,
                cancellationToken);

            var monthlyData = new List<MonthlyUsageDto>();
            for (int i = 1; i <= 12; i++)
            {
                monthlyData.Add(BillingAnalyticsMapper.ToGlobalMonthlyUsageDto(query.Year, i, txs));
            }

            return Result.Success(BillingAnalyticsMapper.ToUsageChartDto(query.Year, monthlyData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingGlobalUsageChart);
            return Result.Failure<UsageChartDto>(BillingMessageConstants.ApiErrorMessages.BillingAnalyticsGlobalChartFailed, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdownAsync(UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-query.Days);

            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.RecordedAt >= startDate,
                cancellationToken);

            var breakdown = usages.GroupBy(u => u.UsageType)
                .Select(BillingAnalyticsMapper.ToUsageSummaryDto)
                .OrderByDescending(x => x.TotalCreditsConsumed)
                .ToList();

            return Result.Success(breakdown.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingGlobalUsageBreakdown);
            return Result.Failure<IEnumerable<UsageSummaryDto>>(BillingMessageConstants.ApiErrorMessages.BillingAnalyticsGlobalBreakdownFailed, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<IEnumerable<TopWorkspaceDto>>> GetTopWorkspacesAsync(UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-query.Days);

            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.RecordedAt >= startDate,
                cancellationToken);

            var topWorkspaces = usages.GroupBy(u => u.WorkspaceId)
                .Select(BillingAnalyticsMapper.ToTopWorkspaceDto)
                .OrderByDescending(x => x.TotalCreditsConsumed)
                .Take(query.Limit)
                .ToList();

            if (topWorkspaces.Any())
            {
                try
                {
                    var ids = topWorkspaces.Select(w => w.WorkspaceId).Distinct().ToArray();
                    var workspaceNames = await _unitOfWork.CreditTransactionRepository.GetWorkspaceNamesAsync(ids, cancellationToken);
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
                    _logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNamesTopWorkspaces);
                }
            }

            return Result.Success(topWorkspaces.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingTopWorkspaces);
            return Result.Failure<IEnumerable<TopWorkspaceDto>>(BillingMessageConstants.ApiErrorMessages.BillingAnalyticsTopWorkspacesFailed, ErrorCodes.InternalServerError);
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
                var subs = await _unitOfWork.Subscriptions.FindAsync(s => subIds.Contains(s.Id), cancellationToken);
                var workspaceIds = new List<Guid>();
                foreach (var s in subs)
                {
                    subIdToWorkspaceId[s.Id] = s.WorkspaceId;
                    workspaceIds.Add(s.WorkspaceId);
                }

                if (workspaceIds.Any())
                {
                    var fetchedNames = await _unitOfWork.CreditTransactionRepository.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);
                    foreach (var kvp in fetchedNames)
                    {
                        workspaceNames[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNamesAlerts);
            }

            var alerts = grouped.Select(g =>
            {
                var wId = subIdToWorkspaceId.TryGetValue(g.SubscriptionId, out var id) ? id : Guid.Empty;
                var workspaceName = workspaceNames.TryGetValue(wId, out var name)
                    ? name
                    : BillingMessageConstants.AnalyticsMessages.UnknownWorkspace;

                return BillingAnalyticsMapper.ToUsageAlertDto(wId, workspaceName, g.ConsumedCredits);
            });

            return Result.Success(alerts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingUsageAlerts);
            return Result.Failure<IEnumerable<UsageAlertDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }
}
