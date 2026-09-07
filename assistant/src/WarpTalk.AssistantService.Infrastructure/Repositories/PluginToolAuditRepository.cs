using Microsoft.EntityFrameworkCore;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PluginToolAuditRepository : GenericRepository<PluginToolAudit>, IPluginToolAuditRepository
{
    private readonly AssistantDbContext _db;

    public PluginToolAuditRepository(AssistantDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PluginToolAudit>> ListForWorkspaceAsync(
        Guid workspaceId,
        string? pluginKey,
        Guid? userId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        // WorkspaceId is nullable on the entity because a tool call made outside any workspace
        // records one. Comparing it to a non-null Guid excludes those rows, which is the point:
        // this read is scoped to what happened IN one workspace, and a personal call belongs to
        // no workspace's Owner.
        var query = _db.Set<PluginToolAudit>().AsNoTracking()
            .Where(audit => audit.WorkspaceId == workspaceId);

        if (!string.IsNullOrWhiteSpace(pluginKey))
            query = query.Where(audit => audit.PluginKey == pluginKey);

        if (userId.HasValue)
            query = query.Where(audit => audit.UserId == userId.Value);

        return await query
            .OrderByDescending(audit => audit.CreatedAt)
            .ThenByDescending(audit => audit.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }
}
