using System;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-14: pure decision logic for whether a scheduled-room reminder should fire for a given
/// window (T-10min / T-1min). Kept independent of the "already sent" persistence and of the
/// polling cadence so it is correct regardless of how often the worker actually runs — a room
/// is reminded exactly once per window as long as `alreadySentAtUtc` is set right after sending.
/// </summary>
public static class ReminderWindowEvaluator
{
    public static readonly TimeSpan TenMinuteWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan OneMinuteWindow = TimeSpan.FromMinutes(1);

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
}
