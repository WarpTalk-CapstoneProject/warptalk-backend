using System;

namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// WT-327: the vocabulary of a recurring room series.
///
/// All three cadences are implemented. WEEKLY and MONTHLY were named here from the start — and
/// accepted by the database CHECK constraint migration 052 installs, along with the
/// by_weekdays/by_month_day columns they need — so switching them on was application code plus a
/// UI control, exactly as intended, and not a second migration against a table that now holds
/// production rows. Nothing outside this file may invent a recurrence type:
/// <see cref="Normalize"/> is the single door.
/// </summary>
public static class RecurrenceTypes
{
    public const string Daily = "DAILY";
    public const string Weekly = "WEEKLY";
    public const string Monthly = "MONTHLY";

    /// <summary>Every value the schema will store. Keep in step with the CHECK constraint.</summary>
    public static readonly string[] All = { Daily, Weekly, Monthly };

    /// <summary>
    /// What the API will actually act on. Kept as its own array rather than folded into
    /// <see cref="All"/> now that the two coincide: it is the seam a future cadence is added
    /// behind (stored by the schema before the materialiser understands it), and collapsing it
    /// would mean re-deriving that seam under time pressure.
    /// </summary>
    public static readonly string[] Supported = { Daily, Weekly, Monthly };

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim().Replace(' ', '_').ToUpperInvariant();
        return Array.IndexOf(All, candidate) >= 0 ? candidate : null;
    }

    public static bool IsSupported(string? normalizedType) =>
        normalizedType is not null && Array.IndexOf(Supported, normalizedType) >= 0;
}

/// <summary>
/// WT-327: ISO-8601 weekday numbering — Monday is 1, Sunday is 7 — which is what
/// <c>recurrence_by_weekdays</c> stores and what the API accepts.
///
/// Deliberately NOT .NET's <see cref="DayOfWeek"/>, where Sunday is 0. The two disagree about
/// every day of the week, and a weekly series that fires a day early is the kind of bug that is
/// only noticed by the people who missed the meeting. One conversion, in one place.
/// </summary>
public static class IsoWeekdays
{
    public const int Monday = 1;
    public const int Sunday = 7;

    public static int Of(DateOnly date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    public static bool IsValid(int weekday) => weekday >= Monday && weekday <= Sunday;
}

/// <summary>WT-327: lifecycle of the series row itself, distinct from any one room's status.</summary>
public static class RecurrenceSeriesStatuses
{
    /// <summary>The materialisation worker will keep extending this series' horizon.</summary>
    public const string Active = "ACTIVE";

    /// <summary>
    /// The host cancelled the whole series. Future occurrences were cancelled with it and the
    /// worker skips the row forever after. Terminal.
    /// </summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>
    /// Every occurrence up to the series' end date exists. Terminal, and reached by the worker
    /// rather than by a user — it is what stops an abandoned demo workspace's series being
    /// re-examined on every sweep for the rest of time.
    /// </summary>
    public const string Completed = "COMPLETED";
}

/// <summary>
/// WT-327: the numbers that bound a series. Deliberately constants rather than configuration:
/// every one of them is a correctness bound (a series must terminate; a sweep must not be able
/// to write unbounded rows), not a tuning knob, and a knob is one deploy away from being set to
/// something that reintroduces the unbounded case.
/// </summary>
public static class RecurrenceLimits
{
    /// <summary>
    /// How far ahead of "now" occurrences are materialised. Two weeks is comfortably past the
    /// furthest a user can see in the schedule UI in one sitting, and short enough that a
    /// cancelled or edited series leaves at most 14 stale rows to clean up.
    /// </summary>
    public const int HorizonDays = 14;

    /// <summary>
    /// Default distance from the first occurrence to the last, when the caller does not name an
    /// end date. A series MUST terminate — see the PR body — and this is the value that makes
    /// "just pick 8am" a bounded request rather than an unbounded one.
    /// </summary>
    public const int DefaultDurationDays = 30;

    /// <summary>Hard ceiling on how far out an explicit end date may be pushed.</summary>
    public const int MaxDurationDays = 365;

    /// <summary>
    /// Most rooms one materialisation pass may create for a single series. A pass that hits
    /// this simply resumes on the next sweep, so the cap costs nothing but bounds the damage
    /// from a clock jump or a bad end date.
    /// </summary>
    public const int MaxOccurrencesPerPass = 32;

    /// <summary>Most series one sweep will look at. Bounds a single pass, not the backlog.</summary>
    public const int MaxSeriesPerSweep = 200;
}

/// <summary>
/// WT-327: every refusal a recurring booking can produce, in one place. These reach the user
/// verbatim — the create-room dialog prints the server's own words (WT-270) — so each one says
/// what to change, not merely what was wrong.
/// </summary>
public static class RecurrenceMessages
{
    public const string RecurrenceRequired = "This request has no recurrence rule.";
    public const string TypeUnrecognised = "Unrecognised repeat option.";
    public const string TypeNotSupportedYet = "{0} repeats are not available yet.";
    public const string TimeMalformed = "Enter the time as HH:mm, for example 08:00.";
    public const string TimeZoneUnknown = "Unrecognised time zone for the repeating schedule.";
    public const string StartDateMalformed = "Enter the first date as YYYY-MM-DD.";
    public const string EndDateMalformed = "Enter the last date as YYYY-MM-DD.";
    public const string StartDateInPast = "A repeating meeting cannot start before today.";
    public const string EndDateBeforeStart = "The last date must be on or after the first date.";
    public const string EndDateTooFar = "A repeating meeting can run for at most {0} days.";
    public const string NoOccurrences = "That schedule produces no meetings.";
    public const string CreateFailed = "An unexpected error occurred while scheduling the repeating meeting.";
    public const string SeriesNotFound = "Repeating schedule not found.";
    public const string OnlyHostMayCancel = "Only the host can cancel a repeating schedule.";
    public const string OnlyHostMayEdit = "Only the host can change a repeating schedule.";
    public const string SeriesAlreadyCancelled = "This repeating schedule has already been cancelled.";
    public const string OccurrenceNotInSeries = "That meeting is not part of this repeating schedule.";
    public const string NoUpcomingOccurrence = "This repeating schedule has no upcoming meeting.";

    // ── Weekly / monthly rule shape ───────────────────────────────────────────

    public const string WeekdayOutOfRange = "Pick weekdays between Monday (1) and Sunday (7).";

    /// <summary>
    /// Sent when a rule field belongs to a different cadence — weekdays on a monthly repeat, a
    /// day-of-month on a weekly one. Refused rather than ignored: the rule the user sees on their
    /// screen and the rule the materialiser follows have to be the same rule, and quietly dropping
    /// half of one is how they stop being.
    /// </summary>
    public const string WeekdaysNotApplicable = "Weekdays apply to a weekly repeat only.";

    public const string MonthDayOutOfRange = "Enter the day of the month as a number from 1 to 31.";
    public const string MonthDayNotApplicable = "A day of the month applies to a monthly repeat only.";

    /// <summary>
    /// A single request cannot carry both a one-off time and a repeat rule: the rule owns every
    /// occurrence's time, so a second, contradictory time would have to be silently discarded —
    /// and a silently discarded field on this dialog is precisely the bug WT-327 removed.
    /// </summary>
    public const string ScheduledAtWithRecurrence =
        "Pick either a one-off date and time or a repeat rule, not both.";
}
