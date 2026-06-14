using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Settings;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceRepository : IGenericRepository<Workspace>
{
    Task<(List<Workspace> Items, int TotalCount)> GetWorkspacesForUserAsync(Guid userId, int page, int pageSize, string? search = null, CancellationToken ct = default);
    Task<WorkspaceConfiguration> GetSettingsAsync(Guid workspaceId, CancellationToken ct = default);
    Task<bool> UpdateSettingsAsync(Guid workspaceId, WorkspaceConfiguration settings, Guid userId, CancellationToken ct = default);
}
