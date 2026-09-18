using System;
using System.Collections.Generic;
using System.Globalization;

namespace WarpTalk.Shared.Contracts.Admin;

/// <summary>
/// Query string for every admin <c>insights</c> endpoint: <c>from</c> (inclusive), <c>to</c>
/// (exclusive), <c>compare</c> = <c>previous</c> (default) | <c>previousMonth</c>, and <c>tz</c>, the
/// IANA time zone whose calendar the day series, "today" and <c>previousMonth</c> follow (default
/// <see cref="AdminComparisonRange.DefaultTimeZoneId"/>). Resolve it with
/// <see cref="AdminComparisonRange.TryResolve(AdminInsightsQuery, out AdminComparisonWindow, out string?, int)"/>.
/// </summary>
public record AdminInsightsQuery : AdminDateRange
{
    public string? Compare { get; init; }

    public string? Tz { get; init; }
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

/// <summary>
/// One calendar day of the requested time zone. <see cref="Start"/> and <see cref="End"/> are the
/// UTC instants of its local midnights, so a Vietnam day <c>2026-09-01</c> is
/// <c>[2026-08-31T17:00Z, 2026-09-01T17:00Z)</c>.
/// </summary>
public readonly record struct AdminLocalDay(DateOnly Date, DateTime Start, DateTime End)
{
    /// <summary>The local date as <c>yyyy-MM-dd</c> — what every per-day row carries on the wire.</summary>
    public string Key => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>The current window, the one it is compared with, and the calendar both are read in.</summary>
public readonly record struct AdminComparisonWindow(
    DateTime From,
    DateTime To,
    DateTime PreviousFrom,
    DateTime PreviousTo,
    string Compare,
    TimeZoneInfo TimeZone)
{
    public AdminInsightRange Range => new(From, To);

    public AdminInsightRange PreviousRange => new(PreviousFrom, PreviousTo);

    /// <summary>The local days of the current window — the x-axis of every per-day series.</summary>
    public IReadOnlyList<AdminLocalDay> Days() => AdminComparisonRange.DaysOf(From, To, TimeZone);
}

/// <summary>
/// Period maths for the admin insights endpoints. Pure: no clock, no I/O.
///
/// <para><b>Instants versus calendars.</b> <c>from</c> and <c>to</c> are absolute instants and are
/// never moved by the time zone. The zone only decides where a DAY or a MONTH begins: the per-day
/// buckets (<see cref="DaysOf"/>), "today", and the <see cref="PreviousMonth"/> shift. The platform's
/// admins are in Vietnam, where a month starts at 17:00Z the day before — bucketed by UTC, every
/// Vietnam day would lose its first seven hours to the day before it.</para>
/// </summary>
public static class AdminComparisonRange
{
    public const string Previous = "previous";
    public const string PreviousMonth = "previousMonth";

    /// <summary>The calendar used when a request names none: where the platform's admins are.</summary>
    public const string DefaultTimeZoneId = "Asia/Ho_Chi_Minh";

    /// <summary>The longest window an insights endpoint accepts.</summary>
    public const int MaxSpanDays = 366;

    private static readonly IReadOnlyDictionary<string, string> CompareModes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Previous] = Previous,
            [PreviousMonth] = PreviousMonth,
        };

    /// <summary>
    /// <see cref="TryResolve(AdminDateRange, string?, string?, out AdminComparisonWindow, out string?, int)"/>
    /// with the query's own <c>compare</c> and <c>tz</c>.
    /// </summary>
    public static bool TryResolve(
        AdminInsightsQuery query,
        out AdminComparisonWindow window,
        out string? error,
        int maxSpanDays = MaxSpanDays)
    {
        ArgumentNullException.ThrowIfNull(query);
        return TryResolve(query, query.Compare, query.Tz, out window, out error, maxSpanDays);
    }

    /// <summary>
    /// Validates the window with <see cref="AdminDateRange.TryNormalize"/>, resolves the time zone and
    /// derives the comparison window. Returns false with a caller-facing message (a 400) for a
    /// backwards or oversized window, an unknown <paramref name="compare"/> or an unknown
    /// <paramref name="timeZoneId"/>. A blank compare means <see cref="Previous"/>; a blank zone
    /// means <see cref="DefaultTimeZoneId"/>.
    /// </summary>
    public static bool TryResolve(
        AdminDateRange range,
        string? compare,
        string? timeZoneId,
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

        if (!TryResolveTimeZone(timeZoneId, out var timeZone, out error))
        {
            return false;
        }

        if (!range.TryNormalize(maxSpanDays, out var from, out var to, out error))
        {
            return false;
        }

        var (previousFrom, previousTo) = mode == PreviousMonth
            ? PreviousMonthOf(from, to, timeZone)
            : PreviousPeriodOf(from, to);

        window = new AdminComparisonWindow(from, to, previousFrom, previousTo, mode, timeZone);
        return true;
    }

    /// <summary>
    /// Resolves an IANA time zone id; blank means <see cref="DefaultTimeZoneId"/>. Returns false with a
    /// caller-facing message rather than throwing — the id is user input, so an unknown one is a 400.
    /// The id is checked for shape before it reaches <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>,
    /// which on Linux reads a file named after it.
    /// </summary>
    public static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo timeZone, out string? error)
    {
        var id = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId.Trim();
        timeZone = TimeZoneInfo.Utc;

        if (!IsPlausibleZoneId(id))
        {
            error = UnknownTimeZone(id);
            return false;
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            error = UnknownTimeZone(id);
            return false;
        }
    }

    /// <summary>The same-length window ending exactly where <paramref name="from"/> begins. Pure length arithmetic; no calendar.</summary>
    public static (DateTime From, DateTime To) PreviousPeriodOf(DateTime from, DateTime to)
    {
        EnsureOrdered(from, to);
        return (from - (to - from), from);
    }

    /// <summary>
    /// <c>[from, to)</c> shifted back one calendar month OF <paramref name="timeZone"/>, clamping the
    /// day of month. Both ends are converted to local wall-clock time, shifted there, and converted
    /// back, so a Vietnam September (<c>[31 Aug 17:00Z, 30 Sep 17:00Z)</c>) compares with the Vietnam
    /// August (<c>[31 Jul 17:00Z, 31 Aug 17:00Z)</c>), not with a UTC August seven hours off.
    ///
    /// The start clamps down (31 Mar → 28 Feb), which <see cref="DateTime.AddMonths"/> already
    /// does. The exclusive END clamps UP to the first local instant of the next month instead: an
    /// end of 31 Mar 00:00 means "through 30 Mar", and the same days in February are "through the
    /// last day of February", i.e. an end of 1 Mar 00:00. Clamping it down like the start would drop
    /// 28 Feb, and for a one-day window on 30 Mar would produce an empty comparison.
    /// </summary>
    public static (DateTime From, DateTime To) PreviousMonthOf(DateTime from, DateTime to, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        EnsureOrdered(from, to);

        var localFrom = ToLocal(from, timeZone);
        var localTo = ToLocal(to, timeZone);

        var previousFromLocal = localFrom.AddMonths(-1);

        var shiftedTo = localTo.AddMonths(-1);
        var previousToLocal = localTo.Day > DateTime.DaysInMonth(shiftedTo.Year, shiftedTo.Month)
            ? new DateTime(localTo.Year, localTo.Month, 1, 0, 0, 0, DateTimeKind.Unspecified)
            : shiftedTo;

        var previousFrom = ToUtc(previousFromLocal, timeZone);
        var previousTo = ToUtc(previousToLocal, timeZone);

        // Unreachable for from < to in a zone without transitions (both shifts are monotone and the
        // clamped end is strictly after any day of the previous month). A DST transition inside the
        // shifted window could in principle collapse it, so an impossible input still cannot yield
        // an inverted window.
        if (previousTo <= previousFrom)
        {
            previousTo = previousFrom + (to - from);
        }

        return (previousFrom, previousTo);
    }

    /// <summary>
    /// Every local calendar day of <paramref name="timeZone"/> that <c>[from, to)</c> touches, in
    /// order. The first day starts at the local midnight on or before <paramref name="from"/>, so a
    /// partial first day is still a whole bucket; the caller zero-fills and clips.
    /// </summary>
    public static IReadOnlyList<AdminLocalDay> DaysOf(DateTime from, DateTime to, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        EnsureOrdered(from, to);

        var days = new List<AdminLocalDay>();
        var date = LocalDateOf(from, timeZone);
        var start = StartOfLocalDay(date, timeZone);
        while (start < to)
        {
            var next = date.AddDays(1);
            var end = StartOfLocalDay(next, timeZone);
            days.Add(new AdminLocalDay(date, start, end));
            date = next;
            start = end;
        }

        return days;
    }

    /// <summary>
    /// The index in <paramref name="days"/> (as returned by <see cref="DaysOf"/>) of the day containing
    /// <paramref name="instant"/>, or -1 when it falls outside all of them.
    /// </summary>
    public static int IndexOfDay(IReadOnlyList<AdminLocalDay> days, DateTime instant)
    {
        ArgumentNullException.ThrowIfNull(days);
        int low = 0, high = days.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (instant < days[mid].Start) high = mid - 1;
            else if (instant >= days[mid].End) low = mid + 1;
            else return mid;
        }

        return -1;
    }

    /// <summary>The local calendar day of <paramref name="timeZone"/> that <paramref name="instant"/> falls on.</summary>
    public static AdminLocalDay LocalDayOf(DateTime instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var date = LocalDateOf(instant, timeZone);
        return new AdminLocalDay(date, StartOfLocalDay(date, timeZone), StartOfLocalDay(date.AddDays(1), timeZone));
    }

    /// <summary>The local date <paramref name="instant"/> (a UTC instant) falls on in <paramref name="timeZone"/>.</summary>
    public static DateOnly LocalDateOf(DateTime instant, TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(ToLocal(instant, timeZone));

    /// <summary>The UTC instant local <paramref name="date"/> begins in <paramref name="timeZone"/>.</summary>
    public static DateTime StartOfLocalDay(DateOnly date, TimeZoneInfo timeZone)
        => ToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone);

    /// <summary>The UTC instant the local calendar month <paramref name="year"/>-<paramref name="month"/> begins in <paramref name="timeZone"/>.</summary>
    public static DateTime StartOfLocalMonth(int year, int month, TimeZoneInfo timeZone)
        => StartOfLocalDay(new DateOnly(year, month, 1), timeZone);

    private static DateTime ToLocal(DateTime instant, TimeZoneInfo timeZone)
    {
        var asUtc = instant.Kind == DateTimeKind.Utc ? instant : DateTime.SpecifyKind(instant, DateTimeKind.Utc);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(asUtc, timeZone), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// A local wall-clock time as a UTC instant. A time swallowed by a spring-forward gap moves to the
    /// moment the clocks jumped; a time repeated by a fall-back takes the EARLIER instant, so a day or
    /// month always begins at its first instant. Vietnam has no DST; this exists because <c>tz</c>
    /// accepts any IANA id.
    /// </summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var wallClock = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(wallClock))
        {
            for (var minutes = 1; minutes <= 24 * 60; minutes++)
            {
                var candidate = wallClock.AddMinutes(minutes);
                if (!timeZone.IsInvalidTime(candidate))
                {
                    wallClock = candidate;
                    break;
                }
            }
        }

        if (timeZone.IsAmbiguousTime(wallClock))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(wallClock);
            var largest = offsets[0];
            foreach (var offset in offsets)
            {
                if (offset > largest) largest = offset;
            }

            return DateTime.SpecifyKind(wallClock - largest, DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(wallClock, timeZone), DateTimeKind.Utc);
    }

    private static bool IsPlausibleZoneId(string id)
    {
        if (id.Length is 0 or > 64 || id.Contains("..", StringComparison.Ordinal) || id.StartsWith('/'))
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+'))
            {
                return false;
            }
        }

        return true;
    }

    private static string UnknownTimeZone(string id)
        => $"Unknown tz '{(id.Length > 64 ? id[..64] : id)}'. Expected an IANA time zone id such as {DefaultTimeZoneId}.";

    private static void EnsureOrdered(DateTime from, DateTime to)
    {
        if (from >= to)
        {
            throw new ArgumentException("'from' must be earlier than 'to'.", nameof(from));
        }
    }
}
