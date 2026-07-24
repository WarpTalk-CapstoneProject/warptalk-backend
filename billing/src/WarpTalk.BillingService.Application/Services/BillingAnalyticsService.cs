using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class BillingAnalyticsService : IBillingAnalyticsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<BillingAnalyticsService> _logger;

    public BillingAnalyticsService(IUnitOfWork unitOfWork, ILogger<BillingAnalyticsService> logger)
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
                return Result.Failure<BillingReportDto>("No active subscription found for this workspace.", ErrorCodes.BillingSubscriptionNotFound);

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

            int totalTopUps = transactions.Where(tx => tx.Type == TransactionConstants.TransactionTypes.TopUp).Sum(tx => tx.Amount);
            int totalConsumed = Math.Abs(transactions.Where(tx => tx.Type == TransactionConstants.TransactionTypes.Consume).Sum(tx => tx.Amount));

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
                workspaceId, query.Month, query.Year, startingBalance, endingBalance,
                totalTopUps, totalConsumed, averageTranslationCost, averageCostPerMeeting, breakdown
            );

            return Result.Success(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating billing report for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<BillingReportDto>("Failed to generate billing report.", ErrorCodes.InternalServerError);
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
                return Result.Failure<UsageChartDto>("No subscription found for this workspace.", ErrorCodes.BillingSubscriptionNotFound);

            var startDate = new DateTime(query.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddYears(1);

            var txs = await _unitOfWork.CreditTransactionRepository.FindAsync(
                tx => subIds.Contains(tx.SubscriptionId) && tx.CreatedAt >= startDate && tx.CreatedAt < endDate,
                cancellationToken);

            var monthlyData = Enumerable.Range(1, 12).Select(month =>
            {
                var monthTxs = txs.Where(t => t.CreatedAt.Month == month).ToList();
                var topUp = monthTxs.Where(t => t.Type == TransactionConstants.TransactionTypes.TopUp).Sum(t => t.Amount);
                var consumed = Math.Abs(monthTxs.Where(t => t.Type == TransactionConstants.TransactionTypes.Consume).Sum(t => t.Amount));

                return new MonthlyUsageDto(
                    month,
                    new DateTime(query.Year, month, 1).ToString("MMM"),
                    consumed,
                    topUp
                );
            }).ToList();

            return Result.Success(new UsageChartDto(query.Year, monthlyData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage chart for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<UsageChartDto>("Failed to generate chart.", ErrorCodes.InternalServerError);
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
            return Result.Failure<IEnumerable<FeatureAdoptionDto>>("Failed to generate feature adoption.", ErrorCodes.InternalServerError);
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
            return Result.Failure<GlobalBillingMetricsDto>("Failed to generate global metrics.", ErrorCodes.InternalServerError);
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
                var monthTxs = txs.Where(t => t.CreatedAt.Month == i).ToList();
                var consumed = monthTxs.Where(t => t.Type == TransactionConstants.TransactionTypes.Consume).Sum(t => Math.Abs(t.Amount));
                var topUp = monthTxs.Where(t => t.Type == TransactionConstants.TransactionTypes.TopUp && t.Amount > 0).Sum(t => t.Amount);

                monthlyData.Add(new MonthlyUsageDto(i, new DateTime(query.Year, i, 1).ToString("MMM"), consumed, topUp));
            }

            return Result.Success(new UsageChartDto(query.Year, monthlyData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global usage chart");
            return Result.Failure<UsageChartDto>("Failed to generate global chart.", ErrorCodes.InternalServerError);
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

    public async Task<Result<IEnumerable<TopWorkspaceDto>>> GetTopWorkspacesAsync(UsageChartQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-query.Days);
            
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
                .Take(query.Limit)
                .ToList();

            if (topWorkspaces.Any())
            {
                try
                {
                    var connection = (Npgsql.NpgsqlConnection)_unitOfWork.GetDbConnection();
                    var wasOpen = connection.State == System.Data.ConnectionState.Open;
                    if (!wasOpen) await connection.OpenAsync(cancellationToken);

                    var ids = topWorkspaces.Select(w => w.WorkspaceId).Distinct().ToArray();
                    using var cmd = new Npgsql.NpgsqlCommand(
                        "SELECT id, name FROM workspace.workspaces WHERE id = ANY(@ids)",
                        connection);
                    cmd.Parameters.AddWithValue("ids", ids);

                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    var workspaceNames = new Dictionary<Guid, string>();
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        workspaceNames.Add(reader.GetFieldValue<Guid>(0), reader.GetString(1));
                    }
                    await reader.CloseAsync();

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
            return Result.Failure<IEnumerable<TopWorkspaceDto>>("Failed to get top workspaces.", ErrorCodes.InternalServerError);
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
                var connection = (Npgsql.NpgsqlConnection)_unitOfWork.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync(cancellationToken);

                using var cmdSubs = new Npgsql.NpgsqlCommand(
                    "SELECT id, workspace_id FROM subscription.subscriptions WHERE id = ANY(@subIds)", connection);
                cmdSubs.Parameters.AddWithValue("subIds", subIds);
                using var readerSubs = await cmdSubs.ExecuteReaderAsync(cancellationToken);
                var workspaceIds = new List<Guid>();
                while (await readerSubs.ReadAsync(cancellationToken))
                {
                    var sId = readerSubs.GetGuid(0);
                    var wId = readerSubs.GetGuid(1);
                    subIdToWorkspaceId[sId] = wId;
                    workspaceIds.Add(wId);
                }
                await readerSubs.CloseAsync();

                if (workspaceIds.Any())
                {
                    using var command = new Npgsql.NpgsqlCommand(
                        "SELECT id, name FROM workspace.workspaces WHERE id = ANY(@ids)", connection);
                    command.Parameters.AddWithValue("ids", workspaceIds.ToArray());

                    using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        workspaceNames.Add(reader.GetFieldValue<Guid>(0), reader.GetString(1));
                    }
                }
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
}
