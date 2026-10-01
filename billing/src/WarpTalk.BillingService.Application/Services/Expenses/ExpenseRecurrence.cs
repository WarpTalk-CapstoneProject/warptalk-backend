using System;
using System.Collections.Generic;
using System.Globalization;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Services.Expenses;

/// <summary>
/// Dates of a recurring expense (G12). Every occurrence is counted from the series' own first date,
/// so a series that starts on the 31st falls on the 30th in April and back on the 31st in May —
/// never drifting to the 28th forever after February. Pure: no clock.
/// </summary>
public static class ExpenseRecurrence
{
    public static int StepMonths(string recurrence) => recurrence switch
    {
        OperatingExpenseConstants.Recurrences.Monthly => 1,
        OperatingExpenseConstants.Recurrences.Yearly => 12,
        _ => 0,
    };

    /// <summary>The <paramref name="index"/>-th occurrence (0 = the anchor itself).</summary>
    public static DateOnly Occurrence(DateOnly anchor, string recurrence, int index)
        => anchor.AddMonths(StepMonths(recurrence) * index);

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/> and on or after <paramref name="notBefore"/>;
    /// null for a non-recurring expense. <paramref name="notBefore"/> is how a series recorded with a past
    /// first date starts from today instead of back-filling every month in between.
    /// </summary>
    public static DateOnly? NextAfter(DateOnly anchor, string recurrence, DateOnly after, DateOnly? notBefore = null)
    {
        var step = StepMonths(recurrence);
        if (step == 0) return null;

        var floor = notBefore is { } nb && nb > after ? nb.AddDays(-1) : after;
        // Jump close to the answer, then walk: at most a couple of iterations.
        var monthsApart = (floor.Year - anchor.Year) * 12 + floor.Month - anchor.Month;
        var index = Math.Max(1, monthsApart / step);
        while (index > 1 && Occurrence(anchor, recurrence, index - 1) > floor) index--;
        while (Occurrence(anchor, recurrence, index) <= floor) index++;
        return Occurrence(anchor, recurrence, index);
    }

    /// <summary>Occurrences of a series from <paramref name="next"/> through <paramref name="until"/>, capped by its end date.</summary>
    public static IEnumerable<DateOnly> Upcoming(DateOnly anchor, string recurrence, DateOnly next, DateOnly until, DateOnly? endDate)
    {
        var step = StepMonths(recurrence);
        if (step == 0) yield break;

        var current = next;
        var guard = 0;
        while (current <= until && (endDate is null || current <= endDate) && guard++ < 400)
        {
            yield return current;
            current = NextAfter(anchor, recurrence, current)!.Value;
        }
    }

    // ── Months ────────────────────────────────────────────────────────────────────────────────

    public static string MonthKey(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>Parses "yyyy-MM" to the first day of that month.</summary>
    public static bool TryParseMonth(string? value, out DateOnly month)
    {
        month = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!DateOnly.TryParseExact(value.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        month = parsed;
        return true;
    }

    /// <summary>First days of every month from <paramref name="from"/> through <paramref name="to"/>.</summary>
    public static IReadOnlyList<DateOnly> MonthsBetween(DateOnly from, DateOnly to)
    {
        var months = new List<DateOnly>();
        for (var month = FirstOfMonth(from); month <= to; month = month.AddMonths(1)) months.Add(month);
        return months;
    }

    public static DateOnly LastDayOfMonth(DateOnly firstOfMonth) => firstOfMonth.AddMonths(1).AddDays(-1);

    /// <summary>
    /// Resolves an optional ["yyyy-MM", "yyyy-MM"] pair: blank <paramref name="to"/> is the current month,
    /// blank <paramref name="from"/> is <paramref name="defaultSpan"/> months ending with it.
    /// </summary>
    public static bool TryResolveMonths(string? from, string? to, DateOnly today, int defaultSpan, int maxSpan,
        out DateOnly fromMonth, out DateOnly toMonth, out string? error)
    {
        error = null;
        fromMonth = default;
        toMonth = FirstOfMonth(today);
        if (!string.IsNullOrWhiteSpace(to) && !TryParseMonth(to, out toMonth))
        {
            error = "to must be a month (yyyy-MM).";
            return false;
        }

        fromMonth = toMonth.AddMonths(-(defaultSpan - 1));
        if (!string.IsNullOrWhiteSpace(from) && !TryParseMonth(from, out fromMonth))
        {
            error = "from must be a month (yyyy-MM).";
            return false;
        }

        if (fromMonth > toMonth)
        {
            error = "from must not be after to.";
            return false;
        }

        var span = (toMonth.Year - fromMonth.Year) * 12 + toMonth.Month - fromMonth.Month + 1;
        if (span > maxSpan)
        {
            error = $"The range may cover at most {maxSpan} months.";
            return false;
        }

        return true;
    }
}
