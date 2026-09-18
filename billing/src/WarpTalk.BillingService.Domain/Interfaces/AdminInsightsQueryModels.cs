using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Domain.Interfaces;

// Read models for the platform admin Insights page (2026-09-17). Each is produced by the repository
// of the table it aggregates; the money and definition rules on top live in
// Application/Services/AdminBillingInsightsCalculator.

/// <summary>Counted paid payments in one currency. <paramref name="Currency"/> is upper-cased.</summary>
public sealed record PaymentCurrencyTotal(string Currency, int Payments, decimal Total);

/// <summary>
/// One counted paid payment: when it was paid (<c>paid_at</c>, else <c>updated_at</c>, as a UTC
/// instant), its upper-cased currency and total. The insights series bucket these on the local
/// days/months of the request's time zone, which SQL grouping by UTC date cannot do.
/// </summary>
public sealed record PaidAmountRow(DateTime At, string Currency, decimal Total);

/// <summary>One payment as the "recent payments" list shows it. WorkspaceId is null when it has no subscription.</summary>
public sealed record RecentPaymentRow(
    Guid Id,
    Guid? WorkspaceId,
    decimal TotalAmount,
    string Currency,
    string Status,
    string Provider,
    string PaymentMethod,
    DateTime At);

/// <summary>
/// Consume-side ledger totals for a window.
///
/// <paramref name="OverageCredits"/> is derived per transaction from <c>balance_after</c>: the part of
/// each charge that took the balance below zero, which is exactly what settle_usage_charge adds to
/// <c>overage_credits_this_cycle</c>.
///
/// <paramref name="CostCoveredCredits"/> / <paramref name="CostCoveredTransactions"/> are the consume
/// rows whose settled rate card carries a <c>provider_unit_cost</c> in the same unit as the usage
/// record; <paramref name="ProviderCostUsd"/> is Σ quantity × provider_unit_cost over those rows only.
///
/// <paramref name="ByChargeType"/> splits the same credits by charge type (usage type when the row
/// has none), so a partial cost can say WHICH usage it leaves out rather than only how much.
/// </summary>
public sealed record ConsumptionTotals(
    long CreditsConsumed,
    int Transactions,
    long OverageCredits,
    long CostCoveredCredits,
    int CostCoveredTransactions,
    decimal ProviderCostUsd,
    IReadOnlyList<ChargeTypeCoverage>? ByChargeType = null);

/// <summary>Consumed credits of one charge type, and how many of them carry a provider cost.</summary>
public sealed record ChargeTypeCoverage(string ChargeType, long Credits, long CoveredCredits)
{
    public long UncoveredCredits => Credits - CoveredCredits;
}

/// <summary>Consumed credits for one charge / usage type.</summary>
public sealed record CreditsByChargeType(string UsageType, long Credits);

/// <summary>Consumed credits for one workspace.</summary>
public sealed record WorkspaceCredits(Guid WorkspaceId, long Credits);

/// <summary>
/// Subscription flow over a window.
///
/// A PAYING subscription is one with at least one paid payment, or a contract price. It starts at
/// its first paid payment (a contract: at creation) and ends — when its status is cancelled or
/// expired — at <c>cancelled_at</c>, or failing that at the end of its paid period.
/// </summary>
/// <param name="NewSubscriptions">Paying subscriptions whose start falls in the window.</param>
/// <param name="TrialsStarted">Trial subscriptions created in the window that have not paid.</param>
/// <param name="CancelledSubscriptions">
/// Paying subscriptions whose end falls in the window and before now, excluding rows replaced by
/// another subscription of the same workspace within an hour (a plan switch, not churn).
/// </param>
/// <param name="ActiveAtStart">Paying subscriptions that had started, and not ended, at the window start.</param>
public sealed record SubscriptionFlowCounts(
    int NewSubscriptions,
    int TrialsStarted,
    int CancelledSubscriptions,
    int ActiveAtStart);

/// <summary>A live subscription whose current period ends soon.</summary>
public sealed record EndingSoonSubscriptionRow(
    Guid WorkspaceId,
    string PlanName,
    DateTime EndsAt,
    bool AutoRenew,
    string Status);

/// <summary>An invoice that is issued and not yet paid.</summary>
public sealed record OutstandingInvoiceRow(
    Guid InvoiceId,
    Guid WorkspaceId,
    decimal Total,
    string Currency,
    DateTime? DueAt);
