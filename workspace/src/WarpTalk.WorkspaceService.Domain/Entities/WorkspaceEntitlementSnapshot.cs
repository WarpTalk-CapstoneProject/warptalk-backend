using System;

namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>
/// WT-263: WorkspaceService's LOCAL copy of what a workspace is entitled to do.
///
/// This table is the reason meeting creation no longer depends on BillingService being reachable.
/// It is written only by the billing.entitlements_changed consumer and read only by enforcement, so
/// the hot path is a primary-key lookup in this service's own database — no RPC, no timeout, no
/// fail-open/fail-closed dilemma. A billing outage becomes invisible: the last known good snapshot
/// keeps serving.
///
/// It is a CACHE OF A DECISION, never an input to one. Nothing here may be recomputed, merged or
/// second-guessed locally; if a value looks wrong the fix belongs in BillingService's
/// EntitlementResolver, which is the only code allowed to compute an entitlement.
/// </summary>
public class WorkspaceEntitlementSnapshot
{
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// The resolved map as a JSON object of <c>key -&gt; {value, source}</c>, stored verbatim from
    /// the event. Kept as the published shape rather than shredded into columns so that adding an
    /// entitlement key needs no migration in every consuming service — the enforcement code asks for
    /// the key it cares about and ignores the rest.
    /// </summary>
    public string EntitlementsJson { get; set; } = "{}";

    public string? PlanSlug { get; set; }

    public bool HasActiveSubscription { get; set; }

    /// <summary>
    /// When BillingService resolved this map. Doubles as the ordering guard: an event that resolved
    /// EARLIER than the stored snapshot is ignored, so an out-of-order or replayed delivery cannot
    /// roll a workspace back to a stale plan.
    /// </summary>
    public DateTime ResolvedAt { get; set; }

    /// <summary>Envelope id of the event that produced this row — the audit trail from a limit back
    /// to the outbox row that set it.</summary>
    public Guid LastEventId { get; set; }

    public DateTime UpdatedAt { get; set; }
}
