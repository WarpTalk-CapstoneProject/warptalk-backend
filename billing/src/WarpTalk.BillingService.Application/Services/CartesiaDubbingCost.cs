using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>One synced Cartesia UTC day: every credit, the speech-to-text part of it, and when it was read.</summary>
public sealed record CartesiaUsageDay(DateOnly Date, long TotalCredits, long SpeechToTextCredits, DateTime SyncedAt)
{
    /// <summary>Credits charged to dubbing: everything except speech-to-text (production STT is not Cartesia).</summary>
    public long DubbingCredits => Math.Max(0, TotalCredits - SpeechToTextCredits);
}

/// <summary>
/// The dubbing provider cost of one period, measured from Cartesia usage where the sync covers the
/// day, and what it replaces in the rate-card estimate.
/// </summary>
public sealed record MeasuredDubbing(
    decimal UsdPerCredit,
    decimal MeasuredCredits,
    decimal MeasuredUsd,
    int MeasuredDays,
    int EstimatedDays,
    int ProRatedDays,
    // Dubbing credits on unsynced days that a rate card priced: the estimated part.
    long EstimatedDubbingCredits,
    decimal ReplacedRateCardUsd,
    long ReplacedCoveredCredits,
    int ReplacedCoveredTransactions,
    long MeasuredDayCredits,
    int MeasuredDayTransactions,
    IReadOnlyDictionary<string, (long Credits, long CoveredCredits)> MeasuredDayByType)
{
    public const string Measured = "measured";
    public const string Mixed = "mixed";
    public const string Estimated = "estimated";

    /// <summary><c>measured</c> when every UTC day of the period was synced, <c>mixed</c> when some were, else <c>estimated</c>.</summary>
    public string Basis => MeasuredDays == 0 ? Estimated : EstimatedDays == 0 ? Measured : Mixed;
}

/// <summary>
/// AI provider cost of the Cartesia-served charge types (<see cref="ProviderUsageConstants.CartesiaChargeTypes"/>)
/// from MEASURED Cartesia credits:
///
///   cost(day) = Cartesia dubbing credits(day) × cartesia_usd_per_credit   (then × fx_rate_usd_vnd)
///
/// for every UTC day the sync covers. Such a day's WarpTalk dubbing rows drop their rate-card
/// estimate (seconds × a per-second price derived from an assumed 12.5 characters/s) and count as
/// covered. A day with no synced Cartesia usage keeps the rate-card estimate — the fallback.
///
/// Cartesia's finest bucket is a UTC day; Insights days are local to the request's tz. A UTC day only
/// partly inside [from, to) contributes the share of its hours that fall inside — an approximation
/// that assumes usage is spread evenly over that day, and is said so in the note.
///
/// Pure: no clock, no I/O.
/// </summary>
public static class CartesiaDubbingCost
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static MeasuredDubbing Measure(
        DateTime from,
        DateTime to,
        DateTime now,
        IReadOnlyList<CartesiaUsageDay> cartesiaDays,
        IReadOnlyList<DailyChargeTypeConsumption> dubbing,
        decimal usdPerCredit)
    {
        var synced = cartesiaDays.ToDictionary(day => day.Date);
        var consumption = dubbing.ToLookup(row => row.UtcDate);
        var today = DateOnly.FromDateTime(now);

        decimal measuredCredits = 0m, replacedUsd = 0m;
        int measuredDays = 0, estimatedDays = 0, proRated = 0, replacedCoveredTx = 0, measuredDayTx = 0;
        long estimatedDubbingCredits = 0, replacedCovered = 0, measuredDayCredits = 0;
        var byType = new Dictionary<string, (long Credits, long CoveredCredits)>(StringComparer.Ordinal);

        foreach (var date in UtcDaysOf(from, to, now))
        {
            var dayStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var dayEnd = dayStart.AddDays(1);
            var rows = consumption[date].ToList();

            // A closed day counts only if it was read after it closed; today counts as of its last read.
            if (!synced.TryGetValue(date, out var day) || (day.SyncedAt < dayEnd && date != today))
            {
                estimatedDays++;
                // Only what a rate card actually priced is an estimate; uncovered dubbing is named by
                // the coverage note instead.
                estimatedDubbingCredits += rows.Sum(row => row.CoveredCredits);
                continue;
            }

            var dataEnd = Min(dayEnd, day.SyncedAt);
            var overlap = Min(Min(dayEnd, to), dataEnd) - Max(dayStart, from);
            var span = dataEnd - dayStart;
            var fraction = span <= TimeSpan.Zero || overlap <= TimeSpan.Zero
                ? 0m
                : Math.Min(1m, (decimal)overlap.Ticks / span.Ticks);
            if (fraction < 1m) proRated++;

            measuredDays++;
            measuredCredits += day.DubbingCredits * fraction;

            foreach (var row in rows)
            {
                replacedUsd += row.CostUsd;
                replacedCovered += row.CoveredCredits;
                replacedCoveredTx += row.CoveredTransactions;
                measuredDayCredits += row.Credits;
                measuredDayTx += row.Transactions;
                var sum = byType.GetValueOrDefault(row.ChargeType);
                byType[row.ChargeType] = (sum.Credits + row.Credits, sum.CoveredCredits + row.CoveredCredits);
            }
        }

        return new MeasuredDubbing(
            usdPerCredit,
            Math.Round(measuredCredits, 2, MidpointRounding.AwayFromZero),
            measuredCredits * usdPerCredit,
            measuredDays,
            estimatedDays,
            proRated,
            estimatedDubbingCredits,
            replacedUsd,
            replacedCovered,
            replacedCoveredTx,
            measuredDayCredits,
            measuredDayTx,
            byType);
    }

    /// <summary>
    /// The consumption totals with the rate-card estimate of every measured day's dubbing rows swapped
    /// for the measured cost, and those rows counted as covered.
    /// </summary>
    public static ConsumptionTotals Apply(ConsumptionTotals totals, MeasuredDubbing measured)
    {
        if (measured.MeasuredDays == 0) return totals;

        var byType = totals.ByChargeType?
            .Select(type => measured.MeasuredDayByType.TryGetValue(type.ChargeType, out var day)
                ? type with { CoveredCredits = type.CoveredCredits - day.CoveredCredits + day.Credits }
                : type)
            .ToList();

        return totals with
        {
            CostCoveredCredits = totals.CostCoveredCredits - measured.ReplacedCoveredCredits + measured.MeasuredDayCredits,
            CostCoveredTransactions = totals.CostCoveredTransactions - measured.ReplacedCoveredTransactions + measured.MeasuredDayTransactions,
            ProviderCostUsd = totals.ProviderCostUsd - measured.ReplacedRateCardUsd + measured.MeasuredUsd,
            ByChargeType = byType,
        };
    }

    /// <summary>
    /// "dubbing measured from Cartesia usage: 12,345 credits × $0.0000392 over 30 UTC days" — and, when
    /// part of the period had no synced usage, how much was estimated instead. Null when there is
    /// neither measured usage nor dubbing to estimate.
    /// </summary>
    public static string? Note(MeasuredDubbing measured, bool filteredToApiKey)
    {
        var parts = new List<string>();
        var price = measured.UsdPerCredit.ToString("0.##########", Invariant);
        var scope = filteredToApiKey ? string.Empty : ", all API keys";

        if (measured.MeasuredDays > 0)
        {
            var days = measured.EstimatedDays == 0
                ? Plural(measured.MeasuredDays, "UTC day")
                : string.Create(Invariant, $"{measured.MeasuredDays} of {measured.MeasuredDays + measured.EstimatedDays} UTC days");
            parts.Add(string.Create(Invariant,
                $"dubbing measured from Cartesia usage: {measured.MeasuredCredits:N0} credits × ${price} over {days}{scope}"));
        }

        if (measured.EstimatedDays > 0 && measured.EstimatedDubbingCredits > 0)
        {
            parts.Add(measured.MeasuredDays == 0
                ? "dubbing estimated from rate cards (12.5 characters/s): no Cartesia usage synced for the period"
                : string.Create(Invariant,
                    $"estimated from rate cards (12.5 characters/s) on the other {Plural(measured.EstimatedDays, "day")} with no synced Cartesia usage"));
        }

        if (measured.ProRatedDays > 0)
        {
            parts.Add(string.Create(Invariant,
                $"Cartesia reports UTC days, so {Plural(measured.ProRatedDays, "day")} at the edges of the period {(measured.ProRatedDays == 1 ? "is" : "are")} pro-rated by hours"));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>The UTC calendar days that [from, min(to, now)) touches.</summary>
    public static IEnumerable<DateOnly> UtcDaysOf(DateTime from, DateTime to, DateTime now)
    {
        var end = Min(to, now);
        if (end <= from) yield break;

        var last = DateOnly.FromDateTime(end.AddTicks(-1));
        for (var day = DateOnly.FromDateTime(from); day <= last; day = day.AddDays(1))
            yield return day;
    }

    private static string Plural(int count, string noun)
        => string.Create(Invariant, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
