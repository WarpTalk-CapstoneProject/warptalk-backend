using System;
using System.Collections.Generic;
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
    /// The local dates a DAILY series should have rooms for, in order.
    ///
    /// <paramref name="alreadyMaterializedThrough"/> is the watermark: generation resumes
    /// strictly AFTER it, never at or before. That one rule is what makes the sweep safe to run
    /// repeatedly and what makes cancelling a single occurrence stick — the sweep can never
    /// revisit a date it has already passed, so a cancelled Tuesday does not come back on
    /// Wednesday's pass.
    /// </summary>
    /// <param name="recurrenceType">Only <see cref="RecurrenceTypes.Daily"/> yields dates today.</param>
    /// <param name="interval">Every N days. 1 for everything the UI can create.</param>
    /// <param name="startsOn">First candidate date, inclusive.</param>
    /// <param name="endsOn">Last candidate date, inclusive. A series always has one.</param>
    /// <param name="alreadyMaterializedThrough">Watermark, exclusive. Null means nothing generated yet.</param>
    /// <param name="horizonThrough">Do not generate past this local date, inclusive.</param>
    /// <param name="maxCount">Hard cap for one pass.</param>
    public static IReadOnlyList<DateOnly> EnumerateOccurrenceDates(
        string recurrenceType,
        int interval,
        DateOnly startsOn,
        DateOnly endsOn,
        DateOnly? alreadyMaterializedThrough,
        DateOnly horizonThrough,
        int maxCount)
    {
        var dates = new List<DateOnly>();

        // WEEKLY/MONTHLY are storable but not materialisable yet. Returning nothing — rather
        // than falling through to daily behaviour — is deliberate: a series that quietly
        // produced the wrong cadence would be worse than one that produces none.
        if (!RecurrenceTypes.IsSupported(recurrenceType)) return dates;
        if (interval < 1) return dates;
        if (maxCount <= 0) return dates;
        if (endsOn < startsOn) return dates;

        var last = horizonThrough < endsOn ? horizonThrough : endsOn;

        // Resume strictly after the watermark, and never before the series' own start.
        var cursor = startsOn;
        if (alreadyMaterializedThrough is DateOnly watermark)
        {
            var resume = watermark.AddDays(1);
            if (resume > cursor)
            {
                // Snap forward to the next date ON the interval grid anchored at startsOn, so a
                // watermark that landed off-grid (an interval change, a manual fix) cannot shift
                // the whole series by a day.
                var offset = resume.DayNumber - startsOn.DayNumber;
                var steps = (offset + interval - 1) / interval;
                cursor = startsOn.AddDays(steps * interval);
            }
        }

        while (cursor <= last && dates.Count < maxCount)
        {
            dates.Add(cursor);
            cursor = cursor.AddDays(interval);
        }

        return dates;
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
