using System.Text.Json;

namespace WarpTalk.MeetingService.Application.Interfaces;

/// <summary>What applying one LiveKit <c>EgressInfo</c> to our own state actually did.</summary>
public enum EgressCompletionOutcome
{
    /// <summary>No room holds this egress id and no room matched its name — nothing to do.</summary>
    RoomNotFound,

    /// <summary>
    /// The room stopped holding the id, but there was no recording to publish: the egress failed,
    /// was aborted, or finished with no file. Distinct from <see cref="Published"/> because it is
    /// the outcome that should never silently look like success.
    /// </summary>
    Cleared,

    /// <summary>The room was cleared AND a durable RecordingCompleted event was published.</summary>
    Published
}

/// <summary>
/// Turns one LiveKit <c>EgressInfo</c> into our own state: clear the room's in-progress id, and
/// publish the durable RecordingCompleted event when there is a file to publish.
///
/// A separate component rather than a private method on <c>MeetingWebhookService</c> because it
/// now has TWO callers — the <c>egress_ended</c> webhook, and the reconciliation sweep that
/// exists precisely because that webhook cannot be relied on (WT-371 #8). Two copies of "what
/// finishing a recording means" would drift, and the copy that drifted would be the one nobody
/// watches: the fallback only runs when the primary path is already broken.
///
/// Both callers hand it the SAME shape — LiveKit's <c>EgressInfo</c>, straight from the webhook
/// body or straight from ListEgress — which is what makes sharing it possible at all.
///
/// Deliberately does NOT save. The unit of work belongs to the caller: the webhook saves once for
/// whatever event it handled, the sweep saves once for the whole batch.
/// </summary>
public interface IEgressCompletion
{
    /// <summary>
    /// Throws when the durable publish fails. That is not an oversight: the webhook caller turns
    /// it into a 500 so LiveKit retries, and the sweep catches it per room and tries again on its
    /// next tick. Swallowing it would drop a finished recording on the floor.
    /// </summary>
    Task<EgressCompletionOutcome> ApplyAsync(JsonElement egressInfo, CancellationToken ct = default);
}
