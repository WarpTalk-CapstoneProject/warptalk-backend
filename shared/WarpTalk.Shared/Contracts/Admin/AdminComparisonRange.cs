using System;
using System.Collections.Generic;

namespace WarpTalk.Shared.Contracts.Admin;

/// <summary>
/// Query string for every admin <c>insights</c> endpoint: <c>from</c> (inclusive), <c>to</c>
/// (exclusive) and <c>compare</c> = <c>previous</c> (default) | <c>previousMonth</c>.
/// Resolve it with <see cref="AdminComparisonRange.TryResolve"/>.
/// </summary>
public record AdminInsightsQuery : AdminDateRange
{
    public string? Compare { get; init; }
}

/// <summary>A resolved <c>[From, To)</c> window, as echoed in every insights response.</summary>
public sealed record AdminInsightRange(DateTime From, DateTime To);

/// <summary>
/// One comparable figure on the insights page.
///
/// <paramref name="Value"/> and <paramref name="Previous"/> are null when the figure cannot be
/// computed honestly, and <paramref name="Note"/> then says why. A 0 always means "counted, and
/// there were none" — never "unknown".
/// </summary>
public sealed record AdminInsightMetric(
    string Id,
    decimal? Value,
    decimal? Previous,
    string Unit,
    bool HigherIsBetter,
    string? Note = null);

/// <summary>The <c>unit</c> vocabulary the web formats by.</summary>
public static class AdminInsightUnits
{
    public const string Money = "money";
    public const string Count = "count";
    public const string Credits = "credits";
    public const string Hours = "hours";
    public const string Percent = "percent";
}

/// <summary>The current window and the one it is compared with.</summary>
public readonly record struct AdminComparisonWindow(
    DateTime From,
    DateTime To,
    DateTime PreviousFrom,
    DateTime PreviousTo,
    string Compare)
{
    public AdminInsightRange Range => new(From, To);

    public AdminInsightRange PreviousRange => new(PreviousFrom, PreviousTo);
}

/// <summary>
/// Period maths for the admin insights endpoints. Pure: no clock, no I/O.
/// </summary>
public static class AdminComparisonRange
{
    public const string Previous = "previous";
    public const string PreviousMonth = "previousMonth";

    /// <summary>The longest window an insights endpoint accepts.</summary>
    public const int MaxSpanDays = 366;

    private static readonly IReadOnlyDictionary<string, string> CompareModes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Previous] = Previous,
            [PreviousMonth] = PreviousMonth,
        };

    /// <summary>
    /// Validates the window with <see cref="AdminDateRange.TryNormalize"/> and derives the
    /// comparison window. Returns false with a caller-facing message (a 400) for a backwards or
    /// oversized window or an unknown <paramref name="compare"/>. A blank compare means
    /// <see cref="Previous"/>.
    /// </summary>
    public static bool TryResolve(
        AdminDateRange range,
        string? compare,
        out AdminComparisonWindow window,
        out string? error,
        int maxSpanDays = MaxSpanDays)
    {
        window = default;

        string mode;
        if (string.IsNullOrWhiteSpace(compare))
        {
            mode = Previous;
        }
        else if (CompareModes.TryGetValue(compare.Trim(), out var resolved))
        {
            mode = resolved;
        }
        else
        {
            error = $"Unknown compare. Expected one of: {Previous}, {PreviousMonth}.";
            return false;
        }

        if (!range.TryNormalize(maxSpanDays, out var from, out var to, out error))
        {
            return false;
        }

        var (previousFrom, previousTo) = mode == PreviousMonth
            ? PreviousMonthOf(from, to)
            : PreviousPeriodOf(from, to);

        window = new AdminComparisonWindow(from, to, previousFrom, previousTo, mode);
        return true;
    }

    /// <summary>The same-length window ending exactly where <paramref name="from"/> begins.</summary>
    public static (DateTime From, DateTime To) PreviousPeriodOf(DateTime from, DateTime to)
    {
        EnsureOrdered(from, to);
        return (from - (to - from), from);
    }

    /// <summary>
    /// <c>[from, to)</c> shifted back one calendar month, clamping the day of month.
    ///
    /// The start clamps down (31 Mar → 28 Feb), which <see cref="DateTime.AddMonths"/> already
    /// does. The exclusive END clamps UP to the first instant of the next month instead: an end of
    /// 31 Mar 00:00 means "through 30 Mar", and the same days in February are "through the last
    /// day of February", i.e. an end of 1 Mar 00:00. Clamping it down like the start would drop
    /// 28 Feb, and for a one-day window on 30 Mar would produce an empty comparison.
    /// </summary>
    public static (DateTime From, DateTime To) PreviousMonthOf(DateTime from, DateTime to)
    {
        EnsureOrdered(from, to);

        var previousFrom = from.AddMonths(-1);

        var shiftedTo = to.AddMonths(-1);
        var previousTo = to.Day > DateTime.DaysInMonth(shiftedTo.Year, shiftedTo.Month)
            ? new DateTime(to.Year, to.Month, 1, 0, 0, 0, to.Kind)
            : shiftedTo;

        // Unreachable for from < to (both shifts are monotone and the clamped end is strictly
        // after any day of the previous month), kept so an impossible input cannot yield an
        // inverted window.
        if (previousTo <= previousFrom)
        {
            previousTo = previousFrom + (to - from);
        }

        return (previousFrom, previousTo);
    }

    /// <summary>
    /// Every UTC calendar day <c>[from, to)</c> touches, as <c>yyyy-MM-dd</c> keys in order — the
    /// x-axis of the per-day series, zero-filled by the caller.
    /// </summary>
    public static IReadOnlyList<DateTime> DaysOf(DateTime from, DateTime to)
    {
        EnsureOrdered(from, to);
        var days = new List<DateTime>();
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            days.Add(DateTime.SpecifyKind(day, DateTimeKind.Utc));
        }

        return days;
    }

    private static void EnsureOrdered(DateTime from, DateTime to)
    {
        if (from >= to)
        {
            throw new ArgumentException("'from' must be earlier than 'to'.", nameof(from));
        }
    }
}
