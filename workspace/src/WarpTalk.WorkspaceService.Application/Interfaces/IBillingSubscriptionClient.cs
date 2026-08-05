using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.Models;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IBillingSubscriptionClient
{
    Task<bool> IsWorkspaceOnActiveTrialAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// WT-262: the workspace's plan entitlements, or <c>null</c> when BillingService could not be
    /// reached and the answer is therefore UNKNOWN.
    ///
    /// The null-vs-value distinction is the whole point of this signature.
    /// <see cref="IsWorkspaceOnActiveTrialAsync"/> collapses an outage to <c>false</c> because it
    /// only ever widens a decision — an unreachable billing means the trial invite cap does not
    /// bite, and the worst case is one extra member. This one narrows: it feeds a quota gate, so an
    /// outage has to stay distinguishable from "the plan allows it", and the caller decides what to
    /// do with the uncertainty. Callers must not treat null as permissive by default.
    /// </summary>
    Task<WorkspaceFeatureAccess?> GetWorkspaceFeatureAccessAsync(Guid workspaceId, CancellationToken ct = default);
}
