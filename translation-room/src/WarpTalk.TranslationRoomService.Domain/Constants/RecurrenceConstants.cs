using System;

namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// WT-327: the vocabulary of a recurring room series.
///
/// Only DAILY is implemented. WEEKLY and MONTHLY are named here — and accepted by the
/// database CHECK constraint the migration installs — precisely so that adding them later is
/// application code plus a UI control, not a second schema migration. Nothing outside this
/// file may invent a recurrence type: <see cref="Normalize"/> is the single door.
/// </summary>
public static class RecurrenceTypes
{
    public const string Daily = "DAILY";
    public const string Weekly = "WEEKLY";
    public const string Monthly = "MONTHLY";

    /// <summary>Every value the schema will store. Keep in step with the CHECK constraint.</summary>
    public static readonly string[] All = { Daily, Weekly, Monthly };

    /// <summary>
    /// What the API will actually act on today. A request for WEEKLY/MONTHLY is refused with a
    /// "not supported yet" message rather than silently stored as a series no worker will ever
    /// materialise — a stored-but-inert series is exactly the dead-switch failure this feature
    /// exists to remove.
    /// </summary>
    public static readonly string[] Supported = { Daily };

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim().Replace(' ', '_').ToUpperInvariant();
        return Array.IndexOf(All, candidate) >= 0 ? candidate : null;
    }

    public static bool IsSupported(string? normalizedType) =>
        normalizedType is not null && Array.IndexOf(Supported, normalizedType) >= 0;
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
    public const string TypeNotSupportedYet = "{0} repeats are not available yet. Only daily repeats can be scheduled.";
    public const string TimeMalformed = "Enter the daily time as HH:mm, for example 08:00.";
    public const string TimeZoneUnknown = "Unrecognised time zone for the daily schedule.";
    public const string StartDateMalformed = "Enter the first date as YYYY-MM-DD.";
    public const string EndDateMalformed = "Enter the last date as YYYY-MM-DD.";
    public const string StartDateInPast = "A repeating meeting cannot start before today.";
    public const string EndDateBeforeStart = "The last date must be on or after the first date.";
    public const string EndDateTooFar = "A repeating meeting can run for at most {0} days.";
    public const string NoOccurrences = "That schedule produces no meetings.";
    public const string StartTooFarAhead = "The first meeting is too far ahead to schedule yet.";
    public const string CreateFailed = "An unexpected error occurred while scheduling the repeating meeting.";
    public const string SeriesNotFound = "Repeating schedule not found.";
    public const string OnlyHostMayCancel = "Only the host can cancel a repeating schedule.";

    /// <summary>
    /// A single request cannot carry both a one-off time and a repeat rule: the rule owns every
    /// occurrence's time, so a second, contradictory time would have to be silently discarded —
    /// and a silently discarded field on this dialog is precisely the bug WT-327 removed.
    /// </summary>
    public const string ScheduledAtWithRecurrence =
        "Pick either a one-off date and time or a daily repeat, not both.";
}
