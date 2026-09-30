using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.DTOs;

// Admin Insights, profit and loss (2026-09-24). camelCase on the wire; these names ARE the contract.
//
// Money is VND unless the name says Usd. Every USD amount is converted at the USD→VND rate of its own
// UTC day (subscription.fx_rates: Stripe's, or an admin override), never at one rate for a whole period.
// The null + note rule holds throughout: a figure that cannot be computed honestly is null with a note,
// and 0 only ever means "counted, and there was none".

/// <summary>
/// <c>GET ~/api/v1/admin/billing/insights/pnl?from&amp;to&amp;compare&amp;tz</c>.
///
/// Metrics: <c>revenue</c>, <c>aiProviderCost</c>, <c>grossMargin</c>, <c>grossMarginPercent</c>,
/// <c>arpa</c> (revenue per active workspace), <c>activeWorkspaces</c> (paid or consumed credits in the
/// period), <c>creditsConsumed</c>. <paramref name="Days"/> are the local days of the window (as
/// revenueByDay), <paramref name="Months"/> the six local months ending with the month of <c>to</c>.
/// <paramref name="TopWorkspaces"/> each carry one credit figure per entry of <paramref name="Days"/>.
/// </summary>
public sealed record AdminProfitAndLossDto(
    AdminInsightRange Range,
    AdminInsightRange PreviousRange,
    DateTime GeneratedAt,
    IReadOnlyList<AdminInsightMetric> Metrics,
    decimal AiCostUsd,
    decimal CostCoveragePercent,
    string? CostNote,
    string? FxNote,
    IReadOnlyList<AdminPnlPeriodDto> Days,
    IReadOnlyList<AdminPnlPeriodDto> Months,
    IReadOnlyList<AdminProviderCostDto> Providers,
    IReadOnlyList<AdminPlanMarginDto> Plans,
    IReadOnlyList<AdminWorkspaceCreditsTrendDto> TopWorkspaces,
    AdminFxRateStatusDto? Fx);

/// <summary>
/// One local day (<c>yyyy-MM-dd</c>) or month (<c>yyyy-MM</c>). <paramref name="AiCost"/> is null when
/// usage in it had no reconstructable provider cost or no USD→VND rate; <paramref name="MarginPercent"/>
/// is null when revenue is null or 0. <paramref name="FxRate"/> is the USD→VND rate of the day (for a
/// month: of its last day with data).
/// </summary>
public sealed record AdminPnlPeriodDto(
    string Key,
    decimal? Revenue,
    decimal? AiCost,
    decimal AiCostUsd,
    decimal? GrossMargin,
    decimal? MarginPercent,
    long Credits,
    decimal CostCoveragePercent,
    int ActiveWorkspaces,
    decimal? Arpa,
    decimal? FxRate,
    IReadOnlyList<AdminProviderPeriodDto> Providers);

/// <summary>A provider's consumption and cost inside one period. <paramref name="CostVnd"/> is null without a rate.</summary>
public sealed record AdminProviderPeriodDto(string Provider, long Credits, decimal CostUsd, decimal? CostVnd);

/// <summary>
/// One AI provider over the window. <paramref name="Credits"/> are WarpTalk credits consumed by usage it
/// serves; <paramref name="CoveredCredits"/> the part with a known provider cost (the rest — e.g. TRANSLATION,
/// which has no provider price — is left out of <paramref name="CostUsd"/> and named in <paramref name="Note"/>).
/// <paramref name="MeasuredUsd"/> is the part of the cost measured from the provider's own usage API (Cartesia).
/// </summary>
public sealed record AdminProviderCostDto(
    string Provider,
    long Credits,
    long CoveredCredits,
    decimal CoveragePercent,
    decimal CostUsd,
    decimal? CostVnd,
    decimal MeasuredUsd,
    IReadOnlyList<AdminProviderServiceDto> Services,
    string? Note);

/// <summary><paramref name="Service"/> is STT | MT | TTS | Voice clone | LLM | Other.</summary>
public sealed record AdminProviderServiceDto(string ChargeType, string Service, long Credits, long CoveredCredits, decimal CostUsd);

/// <summary>
/// Gross margin of one plan over the window: revenue from its paid payments minus the AI cost of the
/// credits consumed under it. Measured (Cartesia) cost is allocated to plans by their share of that UTC
/// day's dubbing credits; measured usage with no WarpTalk dubbing that day sits on the row whose
/// <paramref name="PlanId"/> is null and slug is <c>unattributed</c>.
/// </summary>
public sealed record AdminPlanMarginDto(
    Guid? PlanId,
    string PlanSlug,
    string PlanName,
    decimal? Revenue,
    long Credits,
    decimal? AiCost,
    decimal? GrossMargin,
    decimal? MarginPercent,
    int ActiveWorkspaces,
    decimal? Arpa,
    decimal CostCoveragePercent,
    string? Note);

/// <summary>A top workspace by credits: its total and one figure per local day of the window.</summary>
public sealed record AdminWorkspaceCreditsTrendDto(
    Guid WorkspaceId,
    string? WorkspaceName,
    string? PlanName,
    long Credits,
    IReadOnlyList<long> Days);
