using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-327: everything about "when does this series happen next", as pure functions.
///
/// Kept free of the database and of <c>DateTime.UtcNow</c> — the caller passes the clock in —
/// for the same reason <see cref="ReminderWindowEvaluator"/> is: the window arithmetic is the
/// part that is easy to get subtly wrong and expensive to reproduce against a live worker, so
/// it is the part that has to be unit-testable without one.
/// </summary>
public static class RecurrenceScheduleCalculator
{
    /// <summary>
    /// Resolves an IANA time zone id. Returns null rather than throwing so a caller can turn an
    /// unknown zone into a validation failure instead of a 500 — a bad zone is user input.
    /// </summary>
    public static TimeZoneInfo? ResolveTimeZone(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// The UTC instant for a wall-clock date+time in a given zone.
    ///
    /// The whole point of storing local time plus an IANA id rather than a fixed UTC offset:
    /// "8am daily" must stay 8am to the people in the room even if their zone's rules change,
    /// so the instant is DERIVED here, per occurrence, and never cached.
    ///
    /// The two irregular cases are resolved deterministically rather than thrown:
    ///  - INVALID (a spring-forward gap swallowed the wall clock): move to the first instant
    ///    that does exist, i.e. the moment the clocks jumped. Skipping the day instead would
    ///    silently drop a meeting the host booked.
    ///  - AMBIGUOUS (a fall-back repeated the wall clock): take the FIRST of the two, the one
    ///    still on the pre-transition offset — the earlier instant, so nobody arrives to find
    ///    the meeting already an hour over.
    /// Vietnam has observed no DST since 1975, so neither branch fires for the team's own zone;
    /// they exist because the column accepts any IANA id.
    /// </summary>
    public static DateTime ToUtcInstant(DateOnly localDate, TimeOnly localTime, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var wallClock = localDate.ToDateTime(localTime, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(wallClock))
        {
            // Walk forward a minute at a time out of the gap. Gaps are at most a couple of
            // hours in every rule set that has ever shipped in tzdata; the bound is a
            // safety net, not an expected path.
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
            // GetAmbiguousTimeOffsets returns both candidate offsets. The LARGER offset is the
            // pre-transition (still-daylight) one, and subtracting a larger offset yields the
            // EARLIER instant — which is the one we want.
            var offsets = timeZone.GetAmbiguousTimeOffsets(wallClock);
            var earliest = offsets[0];
            foreach (var offset in offsets)
            {
                if (offset > earliest) earliest = offset;
            }

            return DateTime.SpecifyKind(wallClock - earliest, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(wallClock, timeZone);
    }

    /// <summary>The local calendar date "now" falls on in the series' own zone.</summary>
    public static DateOnly LocalDateOf(DateTime utcInstant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var asUtc = DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(asUtc, timeZone));
    }

    /// <summary>
    /// The local dates a series should have rooms for, in order, for any of the three cadences.
    ///
    /// <paramref name="alreadyMaterializedThrough"/> is the watermark: generation resumes
    /// strictly AFTER it, never at or before. That one rule is what makes the sweep safe to run
    /// repeatedly and what makes cancelling a single occurrence stick — the sweep can never
    /// revisit a date it has already passed, so a cancelled Tuesday does not come back on
    /// Wednesday's pass.
    ///
    /// Every cadence is generated from <paramref name="startsOn"/> forward and then filtered
    /// against the watermark, rather than each one growing its own resume arithmetic. The old
    /// DAILY-only code snapped a cursor onto the interval grid to survive an off-grid watermark;
    /// generating from the anchor gives that property to WEEKLY and MONTHLY for free, because the
    /// grid is never derived from the watermark in the first place.
    /// </summary>
    /// <param name="recurrenceType">One of <see cref="RecurrenceTypes"/>.</param>
    /// <param name="interval">Every N days/weeks/months. 1 for everything the UI can create.</param>
    /// <param name="startsOn">First candidate date, inclusive. Also the anchor of the interval grid.</param>
    /// <param name="endsOn">Last candidate date, inclusive. A series always has one.</param>
    /// <param name="alreadyMaterializedThrough">Watermark, exclusive. Null means nothing generated yet.</param>
    /// <param name="horizonThrough">Do not generate past this local date, inclusive.</param>
    /// <param name="maxCount">Hard cap for one pass.</param>
    /// <param name="byWeekdays">
    /// WEEKLY only: ISO weekdays (Monday 1 … Sunday 7). Null or empty falls back to the weekday
    /// <paramref name="startsOn"/> itself lands on, which is the rule a user who picked a start
    /// date and nothing else means. It is never a silent no-op.
    /// </param>
    /// <param name="byMonthDay">
    /// MONTHLY only: day of the month 1–31. Null falls back to <paramref name="startsOn"/>'s own
    /// day, for the same reason.
    /// </param>
    public static IReadOnlyList<DateOnly> EnumerateOccurrenceDates(
        string recurrenceType,
        int interval,
        DateOnly startsOn,
        DateOnly endsOn,
        DateOnly? alreadyMaterializedThrough,
        DateOnly horizonThrough,
        int maxCount,
        IReadOnlyList<int>? byWeekdays = null,
        int? byMonthDay = null)
    {
        var dates = new List<DateOnly>();

        // A cadence the schema stores but this build does not materialise yields nothing, rather
        // than falling through to daily behaviour: a series that quietly produced the wrong
        // cadence would be worse than one that produces none.
        if (!RecurrenceTypes.IsSupported(recurrenceType)) return dates;
        if (interval < 1) return dates;
        if (maxCount <= 0) return dates;
        if (endsOn < startsOn) return dates;

        var last = horizonThrough < endsOn ? horizonThrough : endsOn;
        if (last < startsOn) return dates;

        foreach (var candidate in EnumerateCandidates(recurrenceType, interval, startsOn, last, byWeekdays, byMonthDay))
        {
            if (alreadyMaterializedThrough is DateOnly watermark && candidate <= watermark) continue;

            dates.Add(candidate);
            if (dates.Count >= maxCount) break;
        }

        return dates;
    }

    /// <summary>
    /// Every date the rule produces between <paramref name="startsOn"/> and <paramref name="last"/>
    /// inclusive, ascending, ignoring the watermark. Lazy, so a caller that wants three dates out
    /// of a year-long series does three iterations' worth of work past the watermark.
    /// </summary>
    private static IEnumerable<DateOnly> EnumerateCandidates(
        string recurrenceType,
        int interval,
        DateOnly startsOn,
        DateOnly last,
        IReadOnlyList<int>? byWeekdays,
        int? byMonthDay)
    {
        switch (recurrenceType)
        {
            case RecurrenceTypes.Daily:
                for (var date = startsOn; date <= last; date = date.AddDays(interval))
                {
                    yield return date;
                }
                break;

            case RecurrenceTypes.Weekly:
            {
                var weekdays = NormalizeWeekdays(byWeekdays) ?? new[] { IsoWeekdays.Of(startsOn) };

                // Anchored on the MONDAY of the week containing startsOn, not on startsOn itself.
                // "Every 2 weeks on Mon and Fri" has to mean the same two weeks whichever of the
                // two days the series happens to begin on; anchoring on the start date would make
                // the fortnight boundary depend on which weekday the user clicked first.
                var anchorMonday = startsOn.AddDays(-(IsoWeekdays.Of(startsOn) - 1));

                for (var weekStart = anchorMonday; weekStart <= last; weekStart = weekStart.AddDays(7 * interval))
                {
                    foreach (var weekday in weekdays)
                    {
                        var date = weekStart.AddDays(weekday - 1);
                        if (date < startsOn || date > last) continue;
                        yield return date;
                    }
                }
                break;
            }

            case RecurrenceTypes.Monthly:
            {
                var dayOfMonth = byMonthDay ?? startsOn.Day;

                // A month too short for the chosen day is SKIPPED, not clamped to its last day.
                // "The 31st" in February means no February meeting, the way Google Calendar reads
                // it; clamping would silently move the meeting to the 28th and, worse, would move
                // it to a different day in each short month.
                var monthStart = new DateOnly(startsOn.Year, startsOn.Month, 1);
                while (monthStart <= last)
                {
                    if (dayOfMonth <= DateTime.DaysInMonth(monthStart.Year, monthStart.Month))
                    {
                        var date = new DateOnly(monthStart.Year, monthStart.Month, dayOfMonth);
                        if (date >= startsOn && date <= last) yield return date;
                    }

                    monthStart = monthStart.AddMonths(interval);
                }
                break;
            }
        }
    }

    /// <summary>
    /// ISO weekdays, de-duplicated and ascending, or null when the caller named none usable.
    /// Ascending matters: the weekly walk emits a week at a time, so unsorted input would emit
    /// dates out of order and the watermark — which is a single "through this date" line — would
    /// then skip whatever it happened to pass.
    /// </summary>
    public static int[]? NormalizeWeekdays(IReadOnlyList<int>? weekdays)
    {
        if (weekdays is null || weekdays.Count == 0) return null;

        var normalized = new SortedSet<int>();
        foreach (var weekday in weekdays)
        {
            if (IsoWeekdays.IsValid(weekday)) normalized.Add(weekday);
        }

        return normalized.Count == 0 ? null : normalized.ToArray();
    }

    /// <summary>
    /// Whether every occurrence this series will ever have now exists, so the row can go
    /// COMPLETED and stop being examined. Compares against the series' end date, NOT the
    /// horizon — a series is finished when it has run out of dates, not when the sweep has
    /// caught up to two weeks out.
    /// </summary>
    public static bool IsFullyMaterialized(DateOnly? materializedThrough, DateOnly endsOn) =>
        materializedThrough is DateOnly through && through >= endsOn;
}
