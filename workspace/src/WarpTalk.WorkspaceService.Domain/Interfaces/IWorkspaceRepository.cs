using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.ReadModels;
using WarpTalk.WorkspaceService.Domain.Settings;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceRepository : IGenericRepository<Workspace>
{
    Task<(List<Workspace> Items, int TotalCount)> GetWorkspacesForUserAsync(Guid userId, int page, int pageSize, string? search = null, CancellationToken ct = default);

    /// <summary>
    /// Every workspace on the platform, filtered and sorted in SQL. Deliberately has no user
    /// parameter: system admins see the directory independently of their own memberships.
    /// </summary>
    Task<(List<WorkspaceDirectoryRow> Items, int TotalCount)> GetAdminDirectoryAsync(
        WorkspaceDirectoryFilter filter,
        CancellationToken ct = default);

    /// <summary>Detail projection for one workspace, including soft-deleted ones.</summary>
    Task<WorkspaceDirectoryRow?> GetAdminDetailAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// The same projection addressed by slug, so the admin portal can put a workspace's own
    /// name in the URL instead of its primary key (WT-560).
    ///
    /// Unambiguous by construction: `workspaces.slug` carries a table-wide UNIQUE constraint
    /// and a soft delete leaves the row in place, so a deleted workspace keeps its slug rather
    /// than freeing it for a namesake. That matters here — the admin portal is the one surface
    /// that has to reach deleted workspaces at all.
    /// </summary>
    Task<WorkspaceDirectoryRow?> GetAdminDetailBySlugAsync(string slug, CancellationToken ct = default);
    Task<WorkspaceConfiguration> GetSettingsAsync(Guid workspaceId, CancellationToken ct = default);
    Task<bool> UpdateSettingsAsync(Guid workspaceId, WorkspaceConfiguration settings, Guid userId, CancellationToken ct = default);
}
