using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WarpTalk.BillingService.Application.DTOs;

// ── The admin workspace page (ERP-style detail), billing half. ─────────────────────────────────
//
// Read: one overview carrying every money fact the page leads with — revenue, credits and their
// burn, plan and trial, invoices, AI provider cost and the resulting P&L — computed with the same
// rules as the platform Insights page, only scoped to one workspace.
//
// Write: every action carries a mandatory reason, is system-admin gated, and is recorded in the
// platform audit log BEFORE it is saved; an action the audit log refuses is not made.

/// <summary>
/// A money figure in VND. <paramref name="Amount"/> is null — never a fabricated 0 — when it cannot
/// be computed, and <paramref name="Note"/> then says why.
/// </summary>
public sealed record AdminWorkspaceMoneyDto(decimal? Amount, string Currency, string? Note);

public sealed record AdminWorkspaceSubscriptionSummaryDto(
    Guid Id,
    Guid PlanId,
    string PlanName,
    string PlanSlug,
    string BillingCycle,
    string Status,
    string ServiceState,
    string? SuspendedReason,
    bool IsTrial,
    DateTime? TrialEndsAt,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd,
    bool AutoRenew,
    int CreditsRemaining,
    int CreditsUsedThisCycle,
    int CreditsPerCycle);

public sealed record AdminWorkspaceInvoiceSummaryDto(
    int Open,
    int Overdue,
    AdminWorkspaceMoneyDto Outstanding,
    DateTime? NextDueAt,
    int? OldestOverdueDays);

/// <summary>
/// One UTC day of the credit burn chart. <paramref name="BalanceAfter"/> is the ledger's own
/// balance after the day's last row (null on a day with no rows — the chart carries the line over).
/// </summary>
public sealed record AdminWorkspaceBurnPointDto(string Date, long Consumed, long Granted, int? BalanceAfter);

/// <summary>
/// A resolved entitlement and, when set, the contract override on the subscription (layer 3 of the
/// resolution order) that the admin page edits.
/// </summary>
public sealed record AdminWorkspaceEntitlementDto(string Key, string Value, string Source, string? ContractOverride);

public sealed record AdminWorkspaceBillingOverviewDto(
    Guid WorkspaceId,
    DateTime From,
    DateTime To,
    AdminWorkspaceSubscriptionSummaryDto? Subscription,
    AdminWorkspaceMoneyDto RevenueLifetime,
    int PaymentsLifetime,
    AdminWorkspaceMoneyDto RevenueInPeriod,
    int PaymentsInPeriod,
    AdminWorkspaceMoneyDto AiProviderCostInPeriod,
    AdminWorkspaceMoneyDto GrossMarginInPeriod,
    long CreditsConsumedInPeriod,
    AdminWorkspaceInvoiceSummaryDto Invoices,
    IReadOnlyList<AdminWorkspaceBurnPointDto> Burn,
    IReadOnlyList<AdminWorkspaceEntitlementDto> Entitlements);

/// <summary>Negative deducts. Bounded to ±1,000,000 per adjustment, the same bar the portal uses.</summary>
public sealed record AdminAdjustWorkspaceCreditsRequest(int Amount, string Reason);

public sealed record AdminWorkspaceChangePlanRequest(Guid PlanId, string Reason);

/// <summary>Moves the trial end (and the trial period end with it) forward by 1–90 days.</summary>
public sealed record AdminExtendTrialRequest(int Days, string Reason);

/// <summary>
/// Grants 1–12 billing periods free: each one renews the cycle's credits and moves the paid-through
/// date forward a month, with no payment and no invoice.
/// </summary>
public sealed record AdminCompPeriodRequest(int Periods, string Reason);

/// <summary>
/// Contract entitlement overrides, keyed by entitlement key. A key mapped to null removes that
/// override; a key absent from the map is left as it is. Numbers for limits, booleans for
/// capabilities.
/// </summary>
public sealed record AdminEntitlementOverridesRequest(
    Dictionary<string, JsonElement?> Overrides,
    string Reason);

public sealed record AdminMarkInvoicePaidRequest(string Reason);

/// <summary>What an action changed, as the admin page shows it after the confirm.</summary>
public sealed record AdminWorkspaceBillingActionResultDto(
    string Action,
    AdminWorkspaceSubscriptionSummaryDto? Subscription,
    AdminCreditTransactionDto? LedgerEntry,
    InvoiceDto? Invoice,
    IReadOnlyList<AdminWorkspaceEntitlementDto>? Entitlements);
