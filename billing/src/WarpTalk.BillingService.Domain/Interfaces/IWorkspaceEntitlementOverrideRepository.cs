using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>
/// WT-263: the workspace self-service override rows feeding the resolver's fourth layer.
///
/// Its own repository, not <c>Repository&lt;T&gt;()</c> off the unit of work — the repo rule here is
/// one interface per entity, and this one needs a workspace-scoped read that a generic surface
/// cannot express.
/// </summary>
public interface IWorkspaceEntitlementOverrideRepository : IGenericRepository<WorkspaceEntitlementOverride>
{
    Task<IReadOnlyList<WorkspaceEntitlementOverride>> GetForWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default);

    Task<WorkspaceEntitlementOverride?> GetAsync(
        Guid workspaceId,
        string entitlementKey,
        CancellationToken ct = default);
}
