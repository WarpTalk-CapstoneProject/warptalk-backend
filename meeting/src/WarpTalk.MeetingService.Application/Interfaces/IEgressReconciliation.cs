using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

/// <summary>
/// Asks LiveKit what actually happened to every recording we still believe is running, and
/// finishes the ones that are over.
///
/// WT-371 #8. Recording had a single completion path — LiveKit's <c>egress_ended</c> webhook —
/// and in production that webhook was never configured on the LiveKit project. The failure was
/// completely silent: <c>StartRoomCompositeEgress</c> returned a real egress id, so the host was
/// told recording had started; LiveKit recorded the meeting and uploaded it to S3; and then
/// nothing. Five rooms held an ActiveEgressId for five days, <c>meeting_tracks</c> stayed empty
/// across 132 meetings, and not one recording artifact was ever written.
///
/// The lesson is not "configure the webhook". It is that a completion path with no way to ask
/// "did it finish?" cannot detect its own failure — it degrades into an indefinite lie rather
/// than an error. This closes that: with the webhook working the sweep finds nothing to do, and
/// without it recordings still complete, a couple of minutes later instead of instantly.
/// </summary>
public interface IEgressReconciliation
{
    /// <summary>
    /// Returns how many rooms stopped holding an in-progress egress on this pass — completed,
    /// failed and aged-out alike, since all three end the "still recording" state.
    ///
    /// Safe to run beside a working webhook: a double delivery of the same egress id is an
    /// idempotent no-op downstream (RecordingCompletedEventProcessor keys on it), so the sweep
    /// never has to guess whether the webhook got there first.
    /// </summary>
    Task<Result<int>> ReconcileAsync(DateTime utcNow, CancellationToken ct = default);
}
