using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// Metric definitions for the admin Insights page (billing half). Pure and static: every rule that
/// turns repository aggregates into a number an operator will act on lives here, and is tested on
/// plain values in AdminBillingInsightsCalculatorTests.
///
/// The standing rule is "never fabricate zero": a figure that cannot be computed honestly is null
/// with a note saying why. A zero is only returned when zero is the true answer (no usage → no cost).
/// </summary>
public static class AdminBillingInsightsCalculator
{
    public const string FxRateConfigKey = "fx_rate_usd_vnd";
    public const string Vnd = PaymentConstants.Currencies.VndAccounting;
    public const string Usd = "USD";

    /// <summary>Months in revenueByMonth, ending with the month of <c>to</c>.</summary>
    public const int RevenueMonths = 6;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>One side (current or previous period) of a comparable metric.</summary>
    public readonly record struct MetricSide(decimal? Value, string? Note)
    {
        public static MetricSide Unavailable(string note) => new(null, note);
    }

    /// <summary>An amount in one currency and how many source rows it sums.</summary>
    public readonly record struct MoneyPart(string Currency, decimal Amount, int Rows);

    /// <summary>
    /// The VND total of some money parts. <see cref="IncludedRows"/> counts the rows that made it
    /// into <see cref="Amount"/>; <see cref="Amount"/> is null when rows existed and none could be
    /// converted.
    /// </summary>
    public sealed record VndTotal(
        decimal? Amount,
        int IncludedRows,
        decimal ConvertedUsd,
        int ConvertedRows,
        int ExcludedRows,
        IReadOnlyList<string> ExcludedCurrencies);

    // ── Currency ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sums money into VND. VND passes through; USD is converted at the admin-editable
    /// <c>billing_pricing_config.fx_rate_usd_vnd</c> when it is set; anything else — or USD with
    /// no rate — is excluded and reported, never guessed.
    /// </summary>
    public static VndTotal ToVnd(IEnumerable<MoneyPart> parts, decimal? fxUsdVnd)
    {
        decimal amount = 0, convertedUsd = 0;
        int included = 0, converted = 0, excluded = 0;
        var excludedCurrencies = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var part in parts)
        {
            var currency = (part.Currency ?? string.Empty).Trim().ToUpperInvariant();
            if (currency == Vnd)
            {
                amount += part.Amount;
                included += part.Rows;
            }
            else if (currency == Usd && fxUsdVnd is > 0)
            {
                amount += part.Amount * fxUsdVnd.Value;
                convertedUsd += part.Amount;
                converted += part.Rows;
                included += part.Rows;
            }
            else
            {
                excluded += part.Rows;
                excludedCurrencies.Add(currency.Length == 0 ? "unknown-currency" : currency);
            }
        }

        var hasOnlyExcluded = included == 0 && excluded > 0;
        return new VndTotal(
            hasOnlyExcluded ? null : RoundVnd(amount),
            included,
            convertedUsd,
            converted,
            excluded,
            excludedCurrencies.ToList());
    }

    public static decimal RoundVnd(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    /// <summary>"includes 180.00 USD converted at 26,300 VND/USD; excludes 2 EUR rows". Null when nothing needed saying.</summary>
    public static string? ConversionNote(VndTotal total, decimal? fxUsdVnd)
    {
        var parts = new List<string>();
        if (total.ConvertedRows > 0 && fxUsdVnd is { } fx)
        {
            parts.Add(string.Create(Invariant,
                $"includes {total.ConvertedUsd:N2} USD converted at {fx:N0} VND/USD (billing_pricing_config.{FxRateConfigKey})"));
        }

        if (total.ExcludedRows > 0)
        {
            var reason = total.ExcludedCurrencies.Contains(Usd) && fxUsdVnd is not > 0
                ? $" (no {FxRateConfigKey} configured)"
                : string.Empty;
            parts.Add(string.Create(Invariant,
                $"excludes {total.ExcludedRows} {string.Join("/", total.ExcludedCurrencies)} rows{reason}"));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    // ── Metric composition ───────────────────────────────────────────────────

    public static AdminInsightMetric Metric(
        string id, string unit, bool higherIsBetter, MetricSide current, MetricSide previous)
    {
        var note = current.Note;
        if (previous.Value is null && previous.Note is { } previousNote && previousNote != current.Note)
        {
            note = note is null ? $"previous period: {previousNote}" : $"{note}; previous period: {previousNote}";
        }

        return new AdminInsightMetric(id, current.Value, previous.Value, unit, higherIsBetter, note);
    }

    /// <summary>revenue = Σ counted paid payments' total, in VND. See IPaymentRepository for "counted".</summary>
    public static (MetricSide Side, VndTotal Total) Revenue(
        IReadOnlyList<PaymentCurrencyTotal> paid, int stripeDuplicatesExcluded, decimal? fxUsdVnd)
    {
        var total = ToVnd(paid.Select(p => new MoneyPart(p.Currency, p.Total, p.Payments)), fxUsdVnd);
        var notes = new List<string>();
        if (ConversionNote(total, fxUsdVnd) is { } conversion) notes.Add(conversion);
        if (stripeDuplicatesExcluded > 0)
        {
            notes.Add(string.Create(Invariant,
                $"{stripeDuplicatesExcluded} Stripe subscription invoice(s) counted once with their checkout"));
        }

        return (new MetricSide(total.Amount, notes.Count == 0 ? null : string.Join("; ", notes)), total);
    }

    public static MetricSide Payments(IReadOnlyList<PaymentCurrencyTotal> paid)
        => new(paid.Sum(p => p.Payments), null);

    public static MetricSide Count(int value, string? note = null) => new(value, note);

    public static MetricSide NewSubscriptions(SubscriptionFlowCounts flows)
        => new(flows.NewSubscriptions, string.Create(Invariant,
            $"Started paying (first paid payment, or contract start); trials excluded — {flows.TrialsStarted} trial(s) started and have not paid"));

    public const string CancelledSubscriptionsNote =
        "Paying subscriptions that ended (cancelled or expired), dated by cancelled_at or else the end of the paid period; plan switches excluded";

    public const string OverageNote =
        "Derived from each consume transaction's balance_after: credits charged below a zero balance";

    /// <summary>
    /// aiProviderCost = Σ usage quantity × rate card provider_unit_cost (USD) over consume rows whose
    /// settled rate card carries a provider cost in the usage record's unit, converted to VND.
    /// </summary>
    public static MetricSide AiProviderCost(ConsumptionTotals consumption, decimal? fxUsdVnd)
    {
        if (consumption.Transactions == 0)
        {
            return new MetricSide(0m, null);
        }

        if (consumption.CostCoveredTransactions == 0)
        {
            return MetricSide.Unavailable(string.Create(Invariant,
                $"cannot be reconstructed: none of the {consumption.Transactions} consume transaction(s) was settled on a rate card with a provider_unit_cost"));
        }

        if (fxUsdVnd is not > 0)
        {
            return MetricSide.Unavailable($"provider cost is in USD and no {FxRateConfigKey} is configured");
        }

        var coverage = Coverage(consumption);
        var note = string.Create(Invariant,
            $"covers {coverage:0.#}% of consumed credits ({consumption.CostCoveredTransactions} of {consumption.Transactions} transactions have a provider cost); USD converted at {fxUsdVnd.Value:N0} VND/USD");
        return new MetricSide(RoundVnd(consumption.ProviderCostUsd * fxUsdVnd.Value), note);
    }

    /// <summary>Share of consumed credits with a reconstructable provider cost, 0–100. 100 when nothing was consumed.</summary>
    public static decimal Coverage(ConsumptionTotals consumption)
        => consumption.CreditsConsumed <= 0
            ? 100m
            : Math.Round(consumption.CostCoveredCredits * 100m / consumption.CreditsConsumed, 1, MidpointRounding.AwayFromZero);

    /// <summary>grossMargin = revenue − aiProviderCost; null if either is null.</summary>
    public static MetricSide GrossMargin(MetricSide revenue, MetricSide aiCost, ConsumptionTotals consumption)
    {
        if (revenue.Value is null) return MetricSide.Unavailable("revenue is unavailable");
        if (aiCost.Value is null) return MetricSide.Unavailable("AI provider cost is unavailable");

        var coverage = Coverage(consumption);
        var note = coverage < 100m
            ? string.Create(Invariant, $"AI cost covers only {coverage:0.#}% of consumed credits, so this margin is overstated")
            : null;
        return new MetricSide(revenue.Value.Value - aiCost.Value.Value, note);
    }

    /// <summary>revenuePerPayment = revenue / the payments that make up revenue; null when there are none.</summary>
    public static MetricSide RevenuePerPayment(MetricSide revenue, VndTotal revenueTotal)
    {
        if (revenue.Value is null) return MetricSide.Unavailable("revenue is unavailable");
        if (revenueTotal.IncludedRows == 0) return MetricSide.Unavailable("no paid payments in range");
        return new MetricSide(RoundVnd(revenue.Value.Value / revenueTotal.IncludedRows), null);
    }

    /// <summary>cancelled / active at month start × 100, 2 decimals; null when nothing was active.</summary>
    public static decimal? ChurnRate(int cancelled, int activeAtStart)
        => activeAtStart <= 0
            ? null
            : Math.Round(cancelled * 100m / activeAtStart, 2, MidpointRounding.AwayFromZero);

    // ── Series ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every local day of the window (<c>AdminComparisonRange.DaysOf</c> in the request's tz), zero-filled,
    /// in VND. A payment belongs to the local day it was paid on. A day whose every payment was in an
    /// unconvertible currency is null, not 0; the note (null when nothing was left out) says what the
    /// series excludes.
    /// </summary>
    public static (IReadOnlyList<AdminRevenueByDayDto> Days, string? Note) RevenueByDay(
        IReadOnlyList<AdminLocalDay> days, IReadOnlyList<PaidAmountRow> payments, decimal? fxUsdVnd)
    {
        var buckets = new List<MoneyPart>[days.Count];
        var counted = new List<MoneyPart>();
        foreach (var payment in payments)
        {
            var index = AdminComparisonRange.IndexOfDay(days, payment.At);
            if (index < 0) continue;
            var part = new MoneyPart(payment.Currency, payment.Total, 1);
            (buckets[index] ??= []).Add(part);
            counted.Add(part);
        }

        var rows = days
            .Select((day, i) => new AdminRevenueByDayDto(day.Key, ToVnd(buckets[i] ?? [], fxUsdVnd).Amount))
            .ToList();
        return (rows, ExclusionNote(ToVnd(counted, fxUsdVnd), fxUsdVnd));
    }

    /// <summary>
    /// The <see cref="RevenueMonths"/> local calendar months of <paramref name="timeZone"/> ending with
    /// the month that the last instant before <paramref name="to"/> falls in, as UTC instants.
    /// </summary>
    public static AdminInsightRange RevenueMonthWindow(DateTime to, TimeZoneInfo timeZone)
    {
        var (first, last) = RevenueMonthBounds(to, timeZone);
        return new AdminInsightRange(
            AdminComparisonRange.StartOfLocalMonth(first.Year, first.Month, timeZone),
            AdminComparisonRange.StartOfLocalMonth(last.AddMonths(1).Year, last.AddMonths(1).Month, timeZone));
    }

    /// <summary>revenueByMonth over <see cref="RevenueMonthWindow"/>, nulls and note as for <see cref="RevenueByDay"/>.</summary>
    public static (IReadOnlyList<AdminRevenueByMonthDto> Months, string? Note) RevenueByMonth(
        DateTime to, TimeZoneInfo timeZone, IReadOnlyList<PaidAmountRow> payments, decimal? fxUsdVnd)
    {
        var (first, _) = RevenueMonthBounds(to, timeZone);
        var months = Enumerable.Range(0, RevenueMonths)
            .Select(i => first.AddMonths(i))
            .Select(month => (
                Month: month,
                Start: AdminComparisonRange.StartOfLocalMonth(month.Year, month.Month, timeZone),
                End: AdminComparisonRange.StartOfLocalMonth(month.AddMonths(1).Year, month.AddMonths(1).Month, timeZone)))
            .ToList();

        var counted = new List<MoneyPart>();
        var rows = months
            .Select(month =>
            {
                var parts = payments
                    .Where(p => p.At >= month.Start && p.At < month.End)
                    .Select(p => new MoneyPart(p.Currency, p.Total, 1))
                    .ToList();
                counted.AddRange(parts);
                return new AdminRevenueByMonthDto(month.Month.ToString("yyyy-MM", Invariant), ToVnd(parts, fxUsdVnd).Amount);
            })
            .ToList();

        return (rows, ExclusionNote(ToVnd(counted, fxUsdVnd), fxUsdVnd));
    }

    /// <summary>A day's revenue in VND with its conversion note — revenueToday / revenueYesterday.</summary>
    public static (decimal? Amount, string? Note) RevenueBetween(
        IReadOnlyList<PaidAmountRow> payments, DateTime from, DateTime to, decimal? fxUsdVnd)
    {
        var total = ToVnd(
            payments.Where(p => p.At >= from && p.At < to).Select(p => new MoneyPart(p.Currency, p.Total, 1)),
            fxUsdVnd);
        return (total.Amount, ConversionNote(total, fxUsdVnd));
    }

    /// <summary>First and last local month (as the first of the month) of the revenueByMonth window.</summary>
    private static (DateOnly First, DateOnly Last) RevenueMonthBounds(DateTime to, TimeZoneInfo timeZone)
    {
        var lastDay = AdminComparisonRange.LocalDateOf(to.AddTicks(-1), timeZone);
        var last = new DateOnly(lastDay.Year, lastDay.Month, 1);
        return (last.AddMonths(-(RevenueMonths - 1)), last);
    }

    /// <summary>Only the "excludes N … rows" half of <see cref="ConversionNote"/> — what a chart left out.</summary>
    public static string? ExclusionNote(VndTotal total, decimal? fxUsdVnd)
        => total.ExcludedRows == 0
            ? null
            : ConversionNote(total with { ConvertedRows = 0, ConvertedUsd = 0 }, fxUsdVnd);

    // ── Snapshot helpers ─────────────────────────────────────────────────────

    public static AdminActiveByCycleDto ActiveByCycle(IEnumerable<AdminSubscriptionRow> recurring)
    {
        int monthly = 0, yearly = 0, other = 0;
        foreach (var row in recurring)
        {
            var cycle = row.BillingCycle?.Trim() ?? string.Empty;
            if (PaymentConstants.BillingCycles.MonthlySpellings.Contains(cycle, StringComparer.OrdinalIgnoreCase)) monthly++;
            else if (PaymentConstants.BillingCycles.YearlySpellings.Contains(cycle, StringComparer.OrdinalIgnoreCase)) yearly++;
            else other++;
        }

        return new AdminActiveByCycleDto(monthly, yearly, other);
    }

    public static bool IsInTrial(AdminSubscriptionRow row, DateTime now)
        => row.TrialEndsAt is { } trialEnd && trialEnd > now;

    public static bool IsPastDue(AdminSubscriptionRow row)
        => row.ServiceState == SubscriptionConstants.ServiceStates.Suspended
           && row.SuspendedReason == SubscriptionConstants.SuspendedReasons.InvoiceOverdue;

    public static IReadOnlyList<AdminSubscriptionsByPlanDto> SubscriptionsByPlan(
        IEnumerable<AdminSubscriptionRow> active, DateTime now)
        => active
            .GroupBy(row => row.PlanSlug, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var trial = group.Count(row => IsInTrial(row, now));
                var pastDue = group.Count(row => !IsInTrial(row, now) && IsPastDue(row));
                return new AdminSubscriptionsByPlanDto(
                    group.Key,
                    group.First().PlanName,
                    group.Count() - trial - pastDue,
                    trial,
                    pastDue);
            })
            .OrderByDescending(plan => plan.Active + plan.Trial + plan.PastDue)
            .ThenBy(plan => plan.PlanSlug, StringComparer.Ordinal)
            .ToList();

    public static string PaymentMethodLabel(string provider, string paymentMethod)
        => provider switch
        {
            PaymentConstants.Providers.Stripe => paymentMethod == PaymentConstants.PaymentMethods.Card ? "Stripe card" : "Stripe",
            PaymentConstants.Providers.InternalInvoice => "Invoice",
            _ => string.IsNullOrWhiteSpace(provider) ? paymentMethod : provider,
        };
}
