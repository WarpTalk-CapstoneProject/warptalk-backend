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
    IReadOnlyList<AdminTopWorkspaceCreditsDto> TopWorkspaces,
    AdminAiProviderCostBasisDto? AiProviderCostBasis = null,
    IReadOnlyList<AdminActiveWorkspacesByMonthDto>? ActiveWorkspacesByMonth = null);

/// <summary>
/// WT-692: workspaces that USED the product in a local calendar month (<c>yyyy-MM</c> of the
/// request's <c>tz</c>) — at least one credit consumption row, i.e. translated or dubbed something.
/// Same six months as <c>revenueByMonth</c>. Paying is not the test: a trial workspace that ran a
/// meeting is active, a paid one that ran none is not.
/// </summary>
public sealed record AdminActiveWorkspacesByMonthDto(string Month, int ActiveWorkspaces);

/// <summary>
/// How the current period's <c>aiProviderCost</c> priced dubbing. <paramref name="Basis"/> is
/// <c>measured</c> (every UTC day of the period had synced Cartesia usage), <c>mixed</c> (some did)
/// or <c>estimated</c> (none did: rate-card seconds × an assumed 12.5 characters/s).
/// <paramref name="CartesiaCredits"/> are the measured dubbing credits inside the period (UTC days at
/// its edges pro-rated by hours); <paramref name="SyncStatus"/> is the sync's status right now.
/// </summary>
public sealed record AdminAiProviderCostBasisDto(
    string Basis,
    int MeasuredDays,
    int EstimatedDays,
    decimal CartesiaCredits,
    decimal CartesiaUsdPerCredit,
    string SyncStatus);

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
    IReadOnlyList<AdminHighUsageAlertDto> HighUsageAlerts,
    AdminCartesiaUsageDto? Cartesia = null);

/// <summary>
/// Cartesia, measured: what the usage sync has read from Cartesia's admin usage API.
///
/// <paramref name="Status"/> is <c>ok</c> | <c>disabled</c> (no admin key configured) | <c>error</c>
/// (the last attempt failed; <paramref name="StatusNote"/> says how) | <c>pending</c> (configured, first
/// sync since start not finished). <paramref name="CreditsThisMonth"/> and <paramref name="CreditsToday"/>
/// are every credit on the account (or on the production key, when <paramref name="FilteredToApiKey"/>)
/// for the UTC calendar month and UTC day — Cartesia buckets by UTC day. Null when nothing was synced
/// for them. <paramref name="RemainingCredits"/> is always null today: Cartesia's API has no balance
/// endpoint, and <paramref name="RemainingCreditsNote"/> says so.
/// </summary>
public sealed record AdminCartesiaUsageDto(
    string Status,
    string? StatusNote,
    bool FilteredToApiKey,
    long? CreditsThisMonth,
    long? CreditsToday,
    long? RemainingCredits,
    string? RemainingCreditsNote,
    DateTime? LastSyncedAt,
    DateTime? LastAttemptAt,
    decimal UsdPerCredit);

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
