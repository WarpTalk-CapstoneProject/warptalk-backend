using System;
using System.Linq;
using System.Linq.Expressions;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>What a reconciliation sweep should do with one abandoned room.</summary>
public enum ReconciliationAction
{
    /// <summary>Re-queue finalization for it.</summary>
    Requeue,

    /// <summary>Stop trying, and say so — this is the attempt that crossed the limit.</summary>
    AbandonAndWarn,

    /// <summary>Already given up on. Skip in silence.</summary>
    Skip,
}

/// <summary>
/// Whether a room that ended without artifacts is still worth retrying.
///
/// Split out from ArtifactsReconciliationWorker for one reason: the room-selection half of that
/// sweep is an EF expression that has to be translatable to SQL, and duplicating it as a C#
/// predicate to make it testable would create two rules that can disagree. THIS half is pure
/// arithmetic that SQL never sees, so it can be pinned without inventing a second source of
/// truth.
///
/// The subtlety worth pinning is the third state. A counter that only knows "under the limit"
/// and "over it" either goes quiet the moment it gives up — leaving no record of a meeting whose
/// summary is never coming — or logs the same warning every five minutes forever. The crossing
/// is its own case, so the giving-up is said exactly once.
/// </summary>
public static class ArtifactsReconciliationPolicy
{
    /// <param name="attempts">The value AFTER incrementing, so the first ever sweep sees 1.</param>
    /// <param name="maxAttempts">ArtifactFinalizationSettings.MaxRecoverySweeps.</param>
    public static ReconciliationAction Decide(int attempts, int maxAttempts)
    {
        if (attempts <= maxAttempts) return ReconciliationAction.Requeue;
        if (attempts == maxAttempts + 1) return ReconciliationAction.AbandonAndWarn;
        return ReconciliationAction.Skip;
    }

    /// <summary>
    /// ONLY A MEETING THAT ACTUALLY HAPPENED. This is the fix, and it is one word wide.
    ///
    /// Both sweeps used to select on <c>TerminalStatuses</c> — ENDED, CANCELLED and EXPIRED — so a
    /// meeting somebody CANCELLED was picked up as "ended with no artifacts" and finalized: two
    /// empty artifacts written for a conversation that never took place, and
    /// ArtifactsFinalizationWorker ringing "Summary ready for &lt;title&gt;" at everyone who had been
    /// invited to it. Cancelling qualified because CancelTranslationRoomAsync sets EndedAt, which
    /// the sweep reads as the clock of a finished meeting.
    ///
    /// The shared constant is left alone deliberately: seven other call sites use TerminalStatuses
    /// to mean "this room is over", which is true of all three. What is not true of all three is
    /// that there is something to summarise.
    ///
    /// WHY THE EXPRESSION LIVES HERE. The class comment above says the room-selection half stayed
    /// in the worker because a C# copy of an EF predicate is two rules that can disagree — and it
    /// is right. This is not a copy: it is THE expression, handed to EF by the worker and compiled
    /// by the test, so what the test proves is what the database is asked.
    /// </summary>
    public const string FinalizableStatus = "ENDED";

    /// <summary>
    /// Rooms that ended, are past the grace period, are still inside the lookback, and have
    /// nothing to show for it.
    /// </summary>
    public static Expression<Func<TranslationRoom, bool>> AbandonedRooms(
        DateTime queuedBefore,
        DateTime endedAfter) =>
        room =>
            room.Status == FinalizableStatus
            && room.EndedAt != null
            && room.EndedAt < queuedBefore
            && room.EndedAt > endedAfter
            && !room.TranslationRoomArtifacts.Any();

    /// <summary>
    /// Rooms that ended recently and DO have artifacts — the candidates for a summary that landed
    /// after the finalizer stopped waiting.
    /// </summary>
    public static Expression<Func<TranslationRoom, bool>> RecentlyEndedWithArtifacts(
        DateTime endedAfter) =>
        room =>
            room.Status == FinalizableStatus
            && room.EndedAt != null
            && room.EndedAt > endedAfter
            && room.TranslationRoomArtifacts.Any();
}
