using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

/// <summary>WT-263: reads and writes the local entitlement snapshot. One interface per entity, per
/// the repository rule in this codebase.</summary>
public interface IWorkspaceEntitlementSnapshotRepository : IGenericRepository<WorkspaceEntitlementSnapshot>
{
    Task<WorkspaceEntitlementSnapshot?> GetForWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
}
