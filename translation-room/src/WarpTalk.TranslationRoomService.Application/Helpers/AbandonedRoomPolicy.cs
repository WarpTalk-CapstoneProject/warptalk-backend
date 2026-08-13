using System;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>What a sweep should do with one live room that currently has nobody in it.</summary>
public enum AbandonedRoomAction
{
    /// <summary>Somebody is in it, or it emptied too recently to be sure. Leave it alone.</summary>
    Leave,

    /// <summary>First time it has been seen empty. Write the timestamp down and wait.</summary>
    StartGrace,

    /// <summary>Empty for longer than the grace period. End it.</summary>
    End,
}

/// <summary>
/// When a meeting nobody is in should be ended.
///
/// WHY ANYTHING HAS TO DECIDE THIS
///     Nothing ends a room when the last person leaves. A room reaches ENDED only if a host
///     presses "End for everyone", and that is a two-call client-side saga with no server-side
///     reconciliation — TranslationRoomService.EndTranslationRoomAsync says so itself: a host
///     transfer, or a network blip between the two calls, "leaves the same orphan". Production
///     has rooms from 9 August still reporting LIVE NOW.
///
///     ExpireTranslationRoomAsync exists and looks like the cure, but it only moves SCHEDULED or
///     WAITING rooms to EXPIRED — it cannot touch an IN_PROGRESS one — and it has no production
///     callers at all.
///
/// WHY IT IS NOT "END IT WHEN THE LAST PARTICIPANT LEAVES"
///     That is the obvious rule and it is wrong twice over. It ends a meeting during the seconds
///     between one person dropping off wifi and reconnecting, and during the gap while a host
///     waits alone for someone to arrive. And it cannot fire at all for the case that produced
///     the stuck rooms — a browser that closed without telling anyone. A sweep with a grace
///     period catches every one of those, including the crashes a leave handler never sees.
///
/// THE GRACE IS MEASURED FROM AN OBSERVATION, NOT FROM THE CLOCK
///     "Empty for 20 minutes" needs to know when it became empty, and no column records that.
///     So the first sweep that finds a room empty writes the time down and does nothing; a later
///     sweep ends it. Two observations, which also means a single bad count — a database blip, a
///     roster mid-write — cannot end a live meeting on its own.
/// </summary>
public static class AbandonedRoomPolicy
{
    /// <summary>
    /// How long a live room may sit empty before it is ended.
    ///
    /// Long enough to cover a reconnect, a host who opened the room early, and a break in a long
    /// meeting. Short enough that a meeting abandoned at 6pm is not still billing minutes and
    /// reporting LIVE NOW the next morning.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(20);

    /// <param name="seatHolders">Participants currently holding a seat in the room.</param>
    /// <param name="emptySince">
    /// When a previous sweep first saw this room empty, or null if none has. Cleared by the
    /// caller as soon as anybody is present, so a room that refills starts its grace over.
    /// </param>
    public static AbandonedRoomAction Decide(int seatHolders, DateTime? emptySince, DateTime now)
    {
        if (seatHolders > 0) return AbandonedRoomAction.Leave;

        if (emptySince is null) return AbandonedRoomAction.StartGrace;

        // Strictly greater: a room observed empty exactly one grace period ago has not yet been
        // empty FOR longer than the grace, and the next sweep is seconds away.
        return now - emptySince.Value > GracePeriod
            ? AbandonedRoomAction.End
            : AbandonedRoomAction.Leave;
    }
}
