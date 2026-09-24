using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>How a day's rate was found.</summary>
public enum FxRateBasis
{
    /// <summary>A rate was recorded for that very UTC day.</summary>
    Exact,
    /// <summary>No rate that day; the last one recorded before it.</summary>
    CarriedForward,
    /// <summary>The day predates every recorded rate; the first one recorded after it.</summary>
    BeforeFirstRecord,
    /// <summary>Nothing recorded at all: the configured billing_pricing_config value.</summary>
    Configured,
    /// <summary>No rate of any kind.</summary>
    None,
}

/// <summary>The USD→VND rate for one UTC day, and where it came from.</summary>
public readonly record struct FxRateResolution(
    DateOnly Day, decimal? Rate, string Source, DateOnly? RateDate, FxRateBasis Basis, DateTime? FetchedAt);

/// <summary>
/// The USD→VND rate of every UTC day, from the recorded history (subscription.fx_rates).
///
/// Per day the most authoritative row wins: an admin's manual override, then Stripe's FX quote, then
/// the rate Stripe applied to a converted charge. A day with no row takes the last recorded day
/// before it (carried forward, and a report says so); a day before any row takes the first one
/// after it; with no row at all, the configured <c>fx_rate_usd_vnd</c>. Pure: no clock, no I/O.
/// </summary>
public sealed class FxRateTable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly SortedList<DateOnly, FxRate> _effective;
    private readonly decimal? _configured;
    private readonly string _configuredLabel;

    private FxRateTable(SortedList<DateOnly, FxRate> effective, decimal? configured, string configuredLabel)
    {
        _effective = effective;
        _configured = configured is > 0 ? configured : null;
        _configuredLabel = configuredLabel;
    }

    public static FxRateTable From(IEnumerable<FxRate> rows, decimal? configured)
    {
        var effective = new SortedList<DateOnly, FxRate>();
        foreach (var group in rows.Where(row => row.Rate > 0).GroupBy(row => row.RateDate))
        {
            effective[group.Key] = group
                .OrderBy(row => FxRateConstants.Precedence(row.Source))
                .ThenByDescending(row => row.FetchedAt)
                .First();
        }

        return new FxRateTable(effective, configured, FxRateConstants.Sources.Configured);
    }

    /// <summary>One rate for every day — a caller (or a test) that has only a number.</summary>
    public static FxRateTable Flat(decimal? rate, string label = FxRateConstants.Sources.Configured)
        => new(new SortedList<DateOnly, FxRate>(), rate, label);

    public bool HasRecordedRates => _effective.Count > 0;

    public FxRateResolution Resolve(DateOnly day)
    {
        if (_effective.Count == 0)
        {
            return _configured is { } configured
                ? new FxRateResolution(day, configured, _configuredLabel, null, FxRateBasis.Configured, null)
                : new FxRateResolution(day, null, FxRateConstants.Sources.Configured, null, FxRateBasis.None, null);
        }

        if (_effective.TryGetValue(day, out var exact)) return Of(day, exact, FxRateBasis.Exact);

        // Binary search for the last recorded day before `day`.
        var keys = _effective.Keys;
        int lo = 0, hi = keys.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid] < day) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        return found >= 0
            ? Of(day, _effective.Values[found], FxRateBasis.CarriedForward)
            : Of(day, _effective.Values[0], FxRateBasis.BeforeFirstRecord);
    }

    public FxRateResolution Resolve(DateTime instantUtc) => Resolve(DateOnly.FromDateTime(instantUtc));

    /// <summary>The effective rate of each recorded day in [from, to], oldest first (the settings history).</summary>
    public IReadOnlyList<FxRateResolution> Recorded(DateOnly from, DateOnly to)
        => _effective
            .Where(pair => pair.Key >= from && pair.Key <= to)
            .Select(pair => Of(pair.Key, pair.Value, FxRateBasis.Exact))
            .ToList();

    private static FxRateResolution Of(DateOnly day, FxRate row, FxRateBasis basis)
        => new(day, row.Rate, row.Source, row.RateDate, basis, DateTime.SpecifyKind(row.FetchedAt, DateTimeKind.Utc));

    /// <summary>
    /// What a figure converted with these rates should say: "USD converted at 25,990 VND/USD (Stripe FX
    /// quote, 2026-09-24)" for one rate, "USD converted at each day's rate (25,950–26,010 VND/USD; Stripe
    /// FX quote, Stripe charge conversion)" for several, plus how many days had to borrow another day's
    /// rate. Null when nothing was converted.
    /// </summary>
    public static string? Describe(IEnumerable<FxRateResolution> used)
    {
        var list = used.Where(r => r.Rate is not null).ToList();
        if (list.Count == 0) return null;

        var rates = list.Select(r => r.Rate!.Value).Distinct().ToList();
        var sources = list.Select(r => SourceLabel(r.Source)).Distinct(StringComparer.Ordinal).ToList();
        string head;
        if (rates.Count == 1)
        {
            var only = list[0];
            var when = only.RateDate is { } date ? ", " + date.ToString("yyyy-MM-dd", Invariant) : string.Empty;
            head = string.Create(Invariant, $"USD converted at {rates[0]:N0} VND/USD ({SourceLabel(only.Source)}{when})");
        }
        else
        {
            head = string.Create(Invariant,
                $"USD converted at each day's rate ({rates.Min():N0}–{rates.Max():N0} VND/USD; {string.Join(", ", sources)})");
        }

        var borrowed = list
            .Where(r => r.Basis is FxRateBasis.CarriedForward or FxRateBasis.BeforeFirstRecord)
            .Select(r => r.Day)
            .Distinct()
            .Count();
        return borrowed == 0
            ? head
            : head + string.Create(Invariant, $"; {borrowed} day{(borrowed == 1 ? "" : "s")} had no rate of {(borrowed == 1 ? "its" : "their")} own and used the nearest recorded one");
    }

    public static string SourceLabel(string source) => source switch
    {
        FxRateConstants.Sources.StripeFxQuote => "Stripe FX quote",
        FxRateConstants.Sources.StripeCharge => "Stripe charge conversion",
        FxRateConstants.Sources.Manual => "manual override",
        FxRateConstants.Sources.Configured => "configured fx_rate_usd_vnd",
        _ => source,
    };
}
