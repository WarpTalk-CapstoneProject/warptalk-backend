using System;
using System.Linq.Expressions;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-14: pure decision logic for whether a scheduled-room reminder should fire for a given
/// window (T-30min / T-10min / T-1min). Kept independent of the "already sent" persistence and of
/// the polling cadence so it is correct regardless of how often the worker actually runs — a room
/// is reminded exactly once per window as long as `alreadySentAtUtc` is set right after sending.
/// </summary>
public static class ReminderWindowEvaluator
{
    /// <summary>WT-326: the third window, added alongside the two WT-14 shipped with.</summary>
    public static readonly TimeSpan ThirtyMinuteWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan TenMinuteWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan OneMinuteWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The earliest any reminder can fire, expressed as a lead time before `scheduled_at`.
    /// Must equal the widest window above; <see cref="SweepCandidateFilter"/> is the only
    /// consumer, and it is wrong the moment a wider window is added without updating this.
    /// </summary>
    public static readonly TimeSpan WidestWindow = ThirtyMinuteWindow;

    /// <summary>
    /// True when `nowUtc` falls inside [scheduledAtUtc - window, scheduledAtUtc) and no
    /// reminder has been sent yet for this window. A room already past its scheduled start
    /// (nowUtc >= scheduledAtUtc) never fires — the meeting has effectively already begun.
    /// </summary>
    public static bool ShouldSendReminder(DateTime scheduledAtUtc, DateTime nowUtc, DateTime? alreadySentAtUtc, TimeSpan window)
    {
        if (alreadySentAtUtc.HasValue) return false;

        var windowStart = scheduledAtUtc - window;
        return nowUtc >= windowStart && nowUtc < scheduledAtUtc;
    }

    /// <summary>
    /// WT-326. The SQL-side prefilter for ReminderNotificationWorker's sweep: every room that
    /// <see cref="ShouldSendReminder"/> could possibly say "yes" to at `nowUtc`, and nothing else.
    /// It lives next to the windows themselves because the two have to agree — a prefilter that
    /// excludes a room the evaluator would have fired for is an invisible, permanent miss.
    ///
    /// STATUS — the WT-326 defect. This used to read `Status == "SCHEDULED"`, which silently
    /// disarmed every room whose host opened the lobby early: TranslationRoomService
    /// .OpenWaitingRoomAsync flips SCHEDULED -> WAITING with no time gate whatsoever (its only
    /// precondition is that the caller is the host), so a host who opened the lobby at any point
    /// before T-10min removed the room from this sweep forever, with nothing logged. WAITING is
    /// therefore swept too. That cannot produce a LATE reminder, because the time gate is not
    /// here — `ShouldSendReminder` refuses to fire at or after `scheduled_at`, whatever the
    /// status. IN_PROGRESS and the terminal statuses are still excluded: they all imply the
    /// meeting has begun or is over, which the time bound below already covers, and listing them
    /// would only add rows the evaluator immediately rejects.
    ///
    /// TIME — the range bound is what keeps the widened status filter cheap. A reminder can only
    /// fire in (nowUtc, nowUtc + WidestWindow], so every other row is dead weight in the result
    /// set. It also means a room that never got stamped (worker down across its whole window,
    /// or a recipient that never recovered) falls out of the sweep by itself once it starts,
    /// instead of accumulating forever. `(status, scheduled_at)` is indexed.
    /// </summary>
    public static Expression<Func<TranslationRoom, bool>> SweepCandidateFilter(DateTime nowUtc)
    {
        var horizon = nowUtc + WidestWindow;

        return room =>
            (room.Status == "SCHEDULED" || room.Status == "WAITING")
            && room.ScheduledAt != null
            && room.ScheduledAt > nowUtc
            && room.ScheduledAt <= horizon
            && (room.Reminder30MinSentAt == null || room.Reminder10MinSentAt == null || room.Reminder1MinSentAt == null);
    }
}
