using System;
using System.Linq.Expressions;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-612 / WT-621 / WT-714: pure decision logic for the two clock edges of a booking — "has this
/// booking's slot arrived?" and "is this booking one nobody ever came to?".
///
/// Until now nothing in the service ever changed a room's status because of the clock:
/// <c>scheduled_at</c> was used to validate the form, draw the calendar and time the reminders,
/// and the room itself sat in SCHEDULED until a human pressed Start. The consequence people
/// actually hit is that a meeting booked for 14:00 does not exist at 14:00 — whoever arrives
/// first has to know they are the one who must open it. The other end of the same silence is a
/// booking nobody attended, which stayed SCHEDULED (and, once WT-612 shipped, OPEN) forever.
///
/// Kept separate from the worker for the same reason <see cref="ReminderWindowEvaluator"/> is:
/// the rule has to be correct regardless of how often the poll happens to run, and that is much
/// easier to hold true — and to test — as a pure function.
/// </summary>
public static class ScheduledOpeningEvaluator
{
    /// <summary>
    /// How late a booking may still be opened — and, read from the other side, how long it has to
    /// be ignored before it counts as missed. One constant on purpose: a room stops being openable
    /// at exactly the moment it expires, and two constants would be two numbers free to drift into
    /// a window where the clock will neither open a booking nor close it.
    ///
    /// A worker that has been down for a week must not wake up and fling open every room anyone
    /// ever booked; and a slot that is long past is a meeting that did not happen, not one that
    /// is about to. Two hours because that number already exists twice — the web calls a booking
    /// "Missed" at <c>MISSED_GRACE_MS</c> = 2h, and the meeting service refuses its join link at
    /// <c>ScheduledStartTime + 2h</c> — so opening past that point would contradict both.
    /// </summary>
    public static readonly TimeSpan MissedBookingGrace = TimeSpan.FromHours(2);

    /// <summary>
    /// True when a booking's slot has arrived and has not yet gone stale. Time only: whether the
    /// room is in a status that may be opened is the caller's business
    /// (<see cref="OpeningSweepCandidateFilter"/> in SQL, the service's own guard at the transition).
    /// </summary>
    public static bool ShouldOpen(DateTime scheduledAtUtc, DateTime nowUtc)
        => nowUtc >= scheduledAtUtc && nowUtc - scheduledAtUtc <= MissedBookingGrace;

    /// <summary>
    /// True when a booking is past its grace: the meeting did not happen. Strictly greater than
    /// the grace, where <see cref="ShouldOpen"/> is less than or equal to it, so the boundary
    /// instant belongs to opening and the two can never both claim the same room.
    /// </summary>
    public static bool HasExpired(DateTime scheduledAtUtc, DateTime nowUtc)
        => nowUtc - scheduledAtUtc > MissedBookingGrace;

    /// <summary>
    /// The SQL-side prefilter for the opening sweep: every room <see cref="ShouldOpen"/> could say
    /// yes to, and nothing else.
    ///
    /// SCHEDULED only. A room whose host already opened the lobby (WAITING) or already started it
    /// is past the point this transition exists for, and the terminal statuses are over. Deleted
    /// rooms are excluded the same way every other sweep excludes them.
    /// </summary>
    public static Expression<Func<TranslationRoom, bool>> OpeningSweepCandidateFilter(DateTime nowUtc)
    {
        var earliest = nowUtc - MissedBookingGrace;

        return room =>
            room.Status == "SCHEDULED"
            && room.DeletedAt == null
            && room.ScheduledAt != null
            && room.ScheduledAt <= nowUtc
            && room.ScheduledAt >= earliest;
    }

    /// <summary>
    /// The SQL-side prefilter for the expiry sweep (WT-714): a booking whose grace has run out and
    /// that is still standing in one of the two statuses the clock itself can leave it in.
    ///
    /// SCHEDULED (nobody ever opened it) or OPEN (the clock unlocked the door and nobody walked
    /// through). There is deliberately no participant check: anyone who actually entered took the
    /// room to IN_PROGRESS, and from there only ENDED or the abandoned sweep applies — so by the
    /// time this predicate can match, "still SCHEDULED or OPEN two hours later" already means
    /// "nobody came", and asking the participant table would only re-derive that at the cost of a
    /// join per sweep.
    ///
    /// WAITING is not here even though <c>ExpireTranslationRoomAsync</c> accepts it: a lobby is a
    /// host sitting in the room waiting for people, which is presence, and ending it belongs to
    /// the idle (<see cref="AbandonedRoomPolicy"/> and the idle monitor) sweeps that measure
    /// emptiness rather than to a rule that only knows what was booked.
    /// </summary>
    public static Expression<Func<TranslationRoom, bool>> ExpirySweepCandidateFilter(DateTime nowUtc)
    {
        var cutoff = nowUtc - MissedBookingGrace;

        return room =>
            (room.Status == "SCHEDULED" || room.Status == "OPEN")
            && room.DeletedAt == null
            && room.ScheduledAt != null
            && room.ScheduledAt < cutoff;
    }
}
