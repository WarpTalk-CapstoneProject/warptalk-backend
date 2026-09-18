using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.DTOs;

// Admin Insights, billing half (contract sections 1 and 2, 2026-09-17). Serialized camelCase by the
// API's default JSON options, so property names here ARE the wire contract the web codes against.

/// <summary>
/// <c>GET ~/api/v1/admin/billing/insights</c>. <paramref name="RevenueByDayNote"/> and
/// <paramref name="RevenueByMonthNote"/> say which payments a series left out (a currency with no
/// FX rate); null when it left nothing out.
/// </summary>
public sealed record AdminBillingInsightsDto(
    AdminInsightRange Range,
    AdminInsightRange PreviousRange,
    DateTime GeneratedAt,
    IReadOnlyList<AdminInsightMetric> Metrics,
    IReadOnlyList<AdminRevenueByDayDto> RevenueByDay,
    string? RevenueByDayNote,
    IReadOnlyList<AdminRevenueByMonthDto> RevenueByMonth,
    string? RevenueByMonthNote,
    IReadOnlyList<AdminCreditsByServiceDto> CreditsByService,
    IReadOnlyList<AdminTopWorkspaceCreditsDto> TopWorkspaces);

/// <summary>
/// <paramref name="Date"/> is a local calendar date of the request's <c>tz</c>, <c>yyyy-MM-dd</c>.
/// Revenue in VND; null only when that day's every payment was in a currency that could not be
/// converted (see the series note), never 0 for "unknown".
/// </summary>
public sealed record AdminRevenueByDayDto(string Date, decimal? Revenue);

/// <summary><paramref name="Month"/> is a local calendar month of the request's <c>tz</c>, <c>yyyy-MM</c>. Revenue as for a day.</summary>
public sealed record AdminRevenueByMonthDto(string Month, decimal? Revenue);

public sealed record AdminCreditsByServiceDto(string UsageType, long Credits);

/// <summary><paramref name="WorkspaceName"/> is null when workspace-service could not resolve it.</summary>
public sealed record AdminTopWorkspaceCreditsDto(Guid WorkspaceId, string? WorkspaceName, long Credits);

/// <summary>
/// <c>GET ~/api/v1/admin/billing/insights/snapshot?tz</c> — "right now", no period. "Today",
/// "yesterday" and the churn month are local to <c>tz</c>. <paramref name="RevenueToday"/> and
/// <paramref name="RevenueYesterday"/> are null when every payment of that day was in an
/// unconvertible currency; their notes say what was converted or left out (null when nothing was).
/// </summary>
public sealed record AdminBillingSnapshotDto(
    DateTime GeneratedAt,
    decimal? RevenueToday,
    string? RevenueTodayNote,
    decimal? RevenueYesterday,
    string? RevenueYesterdayNote,
    decimal? Mrr,
    string? MrrNote,
    int ActiveSubscriptions,
    AdminActiveByCycleDto ActiveByCycle,
    AdminChurnRateMonthDto ChurnRateMonth,
    int Trials,
    int TrialsEndingThisWeek,
    int PastDue,
    int Suspended,
    int ActiveWorkspaces,
    long PlatformCreditBalance,
    AdminOutstandingInvoicesDto OutstandingInvoices,
    int OpenSalesLeads,
    IReadOnlyList<AdminSubscriptionsByPlanDto> SubscriptionsByPlan,
    IReadOnlyList<AdminRecentPaymentDto> RecentPayments,
    IReadOnlyList<AdminEndingSoonDto> EndingSoon,
    IReadOnlyList<AdminHighUsageAlertDto> HighUsageAlerts);

public sealed record AdminActiveByCycleDto(int Monthly, int Yearly, int Other);

/// <summary><paramref name="Rate"/> is a percentage with 2 decimals; null when nothing was active at month start.</summary>
public sealed record AdminChurnRateMonthDto(int Cancelled, int AtMonthStart, decimal? Rate);

/// <summary>
/// <paramref name="Amount"/> is in VND and null when no outstanding invoice's currency could be
/// converted; <paramref name="AmountNote"/> says what was converted or left out.
/// <paramref name="OldestPastDueWorkspace"/> is a workspace name (null if unresolved or none).
/// </summary>
public sealed record AdminOutstandingInvoicesDto(
    int Count,
    decimal? Amount,
    string? AmountNote,
    int PastDueCount,
    int? OldestPastDueDays,
    string? OldestPastDueWorkspace);

public sealed record AdminSubscriptionsByPlanDto(string PlanSlug, string PlanName, int Active, int Trial, int PastDue);

/// <summary><paramref name="Amount"/> is in the payment's own <paramref name="Currency"/>.</summary>
public sealed record AdminRecentPaymentDto(
    Guid? WorkspaceId,
    string? WorkspaceName,
    decimal Amount,
    string Currency,
    string Status,
    string Method,
    DateTime At);

public sealed record AdminEndingSoonDto(
    Guid WorkspaceId,
    string? WorkspaceName,
    string PlanName,
    DateTime EndsAt,
    bool CancelAtPeriodEnd);

public sealed record AdminHighUsageAlertDto(Guid WorkspaceId, string? WorkspaceName, long Credits24h);
