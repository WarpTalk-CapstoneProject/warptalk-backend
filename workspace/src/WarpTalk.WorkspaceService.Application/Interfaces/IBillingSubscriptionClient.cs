using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IBillingSubscriptionClient
{
    Task<bool> IsWorkspaceOnActiveTrialAsync(Guid workspaceId, CancellationToken ct = default);

    // WT-263: GetWorkspaceFeatureAccessAsync is GONE, and its absence is the point of the ticket.
    //
    // WT-262 phase 1 put a synchronous plan-quota lookup on the meeting-creation path and had to
    // fail closed, because a quota gate that cannot tell "unknown" from "allowed" is not a gate.
    // Its own author flagged that as the wrong long-term design. Enforcement now reads a locally
    // replicated snapshot kept fresh by billing.entitlements_changed, so there is no remote call on
    // the hot path, no fail-open/fail-closed dilemma to resolve, and a billing outage is invisible
    // to meeting creation.
    //
    // Do not reintroduce a read-through method here. If enforcement needs a new entitlement, add the
    // key to BillingService's EntitlementResolver and read it from the snapshot.

    /// <summary>
    /// WT-263: pushes the workspace's own (tightening-only) entitlement settings to BillingService,
    /// which owns the resolution order and rejects anything that would loosen a plan limit.
    ///
    /// A WRITE-path call, deliberately — it runs when an owner saves settings, never when a meeting
    /// is created. Returns the rejection reason when billing refused the value, or null when it was
    /// accepted (or when billing could not be reached, which must not fail the settings save).
    /// </summary>
    Task<string?> ApplyWorkspaceEntitlementOverridesAsync(
        Guid workspaceId,
        IReadOnlyDictionary<string, string> overrides,
        Guid setByUserId,
        CancellationToken ct = default);
}
