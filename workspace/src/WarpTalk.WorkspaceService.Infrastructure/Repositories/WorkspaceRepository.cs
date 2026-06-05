using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;


namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceRepository : GenericRepository<Workspace>, IWorkspaceRepository
{
    public WorkspaceRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<(List<Workspace> Items, int TotalCount)> GetWorkspacesForUserAsync(Guid userId, int page, int pageSize, string? search = null, CancellationToken ct = default)
    {
        var query = _context.Workspaces
            .AsNoTracking()
            .Where(w => w.WorkspaceMembers.Any(m => m.UserId == userId && m.RemovedAt == null));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(w => w.Name.ToLower().Contains(searchLower) || w.Slug.ToLower().Contains(searchLower));
        }

        return await query
            .OrderByDescending(w => w.CreatedAt)
            .ToPagedListAsync(page, pageSize, ct);
    }

    public async Task<WorkspaceConfiguration> GetSettingsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var workspace = await GetByIdAsync(workspaceId, ct);
        if (workspace == null)
        {
            return new WorkspaceConfiguration();
        }

        var settings = new WorkspaceConfiguration();
        if (!string.IsNullOrWhiteSpace(workspace.Settings) && workspace.Settings != "{}")
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<WorkspaceConfiguration>(workspace.Settings);
                if (parsed != null)
                {
                    settings = parsed;
                }
            }
            catch
            {
                // Fallback to default settings
            }
        }
        return settings;
    }

    public async Task<bool> UpdateSettingsAsync(Guid workspaceId, WorkspaceConfiguration settings, Guid userId, CancellationToken ct = default)
    {
        var workspace = await GetByIdAsync(workspaceId, ct);
        if (workspace == null)
        {
            return false;
        }

        workspace.Settings = JsonSerializer.Serialize(settings);
        workspace.UpdatedAt = DateTime.UtcNow;
        workspace.UpdatedBy = userId;

        Update(workspace);
        return true;
    }
}

