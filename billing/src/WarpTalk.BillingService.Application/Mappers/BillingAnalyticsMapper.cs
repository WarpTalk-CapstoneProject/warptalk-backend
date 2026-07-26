using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class BillingAnalyticsMapper
{
    public static UsageBreakdownDto ToUsageBreakdownDto(IGrouping<string, UsageRecord> usageGroup) =>
        new(
            usageGroup.Key,
            usageGroup.Sum(x => x.CreditsConsumed),
            usageGroup.Sum(x => x.Quantity));

    public static BillingReportDto ToBillingReportDto(
        Guid workspaceId,
        BillingReportQuery query,
        int startingBalance,
        int endingBalance,
        int totalTopUps,
        int totalConsumed,
        decimal? averageTranslationCost,
        int? averageCostPerMeeting,
        IReadOnlyList<UsageBreakdownDto> breakdown) =>
        new(
            workspaceId,
            query.Month,
            query.Year,
            startingBalance,
            endingBalance,
            totalTopUps,
            totalConsumed,
            averageTranslationCost,
            averageCostPerMeeting,
            breakdown);

    public static MonthlyUsageDto ToMonthlyUsageDto(int year, int month, IEnumerable<CreditTransaction> transactions)
    {
        var monthTransactions = transactions.Where(t => t.CreatedAt.Month == month).ToList();
        var topUp = monthTransactions
            .Where(t => t.Type == TransactionConstants.TransactionTypes.TopUp)
            .Sum(t => t.Amount);
        var consumed = Math.Abs(monthTransactions
            .Where(t => t.Type == TransactionConstants.TransactionTypes.Consume)
            .Sum(t => t.Amount));

        return new MonthlyUsageDto(month, new DateTime(year, month, 1).ToString("MMM"), consumed, topUp);
    }

    public static MonthlyUsageDto ToGlobalMonthlyUsageDto(int year, int month, IEnumerable<CreditTransaction> transactions)
    {
        var monthTransactions = transactions.Where(t => t.CreatedAt.Month == month).ToList();
        var consumed = monthTransactions
            .Where(t => t.Type == TransactionConstants.TransactionTypes.Consume)
            .Sum(t => Math.Abs(t.Amount));
        var topUp = monthTransactions
            .Where(t => t.Type == TransactionConstants.TransactionTypes.TopUp && t.Amount > 0)
            .Sum(t => t.Amount);

        return new MonthlyUsageDto(month, new DateTime(year, month, 1).ToString("MMM"), consumed, topUp);
    }

    public static UsageChartDto ToUsageChartDto(int year, IReadOnlyList<MonthlyUsageDto> monthlyData) =>
        new(year, monthlyData);

    public static FeatureAdoptionDto ToFeatureAdoptionDto(IGrouping<string, UsageRecord> usageGroup) =>
        new(
            usageGroup.Key,
            usageGroup.Count(),
            usageGroup.Sum(x => x.CreditsConsumed));

    public static GlobalBillingMetricsDto ToGlobalBillingMetricsDto(
        int totalBalance,
        int activeWorkspaces,
        int monthlyUsage,
        int auditEventsLast30Days) =>
        new(totalBalance, activeWorkspaces, monthlyUsage, auditEventsLast30Days);

    public static UsageSummaryDto ToUsageSummaryDto(IGrouping<string, UsageRecord> usageGroup) =>
        new(usageGroup.Key, usageGroup.Sum(x => x.CreditsConsumed));

    public static TopWorkspaceDto ToTopWorkspaceDto(IGrouping<Guid, UsageRecord> workspaceGroup) =>
        new(
            workspaceGroup.Key,
            string.Format(
                BillingMessageConstants.AnalyticsMessages.WorkspaceNameTemplate,
                workspaceGroup.Key.ToString()[..8].ToUpperInvariant()),
            workspaceGroup.Sum(x => x.CreditsConsumed));

    public static UsageAlertDto ToUsageAlertDto(
        Guid workspaceId,
        string workspaceName,
        int consumedCreditsIn24h) =>
        new(
            WorkspaceId: workspaceId,
            WorkspaceName: workspaceName,
            ConsumedCreditsIn24h: consumedCreditsIn24h,
            Reason: string.Format(
                BillingMessageConstants.AnalyticsMessages.HighConsumptionAlertTemplate,
                consumedCreditsIn24h));
}
