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
}
