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

    public async Task<(IReadOnlyList<PluginToolAudit> Items, int TotalCount)> ListForPluginAsync(
        Guid pluginId,
        Guid? userId,
        string? resultStatus,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var query = _db.PluginToolAudits.Where(audit => audit.PluginId == pluginId);

        if (userId is { } filteredUserId)
            query = query.Where(audit => audit.UserId == filteredUserId);

        if (!string.IsNullOrWhiteSpace(resultStatus))
            query = query.Where(audit => audit.ResultStatus == resultStatus);

        // Counted before paging, so the caller can report a total that does not change as it walks
        // the pages.
        var totalCount = await query.CountAsync(ct);

        // Newest first, then by id: created_at has no uniqueness, and two audits written in the
        // same tick would otherwise be free to swap places between page 1 and page 2, which is how
        // a paged listing quietly drops and duplicates rows.
        var items = await query
            .OrderByDescending(audit => audit.CreatedAt)
            .ThenByDescending(audit => audit.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<int> CountForPluginAsync(Guid pluginId, CancellationToken ct = default)
        => await _db.PluginToolAudits.CountAsync(audit => audit.PluginId == pluginId, ct);

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
        var query = _db.PluginToolAudits.AsNoTracking()
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

    public async Task<IReadOnlyDictionary<Guid, int>> CountDistinctUsersByPluginForWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        var counts = await _db.PluginToolAudits.AsNoTracking()
            .Where(audit => audit.WorkspaceId == workspaceId && audit.ResultStatus == "success")
            .GroupBy(audit => audit.PluginId)
            .Select(group => new { PluginId = group.Key, Count = group.Select(audit => audit.UserId).Distinct().Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(entry => entry.PluginId, entry => entry.Count);
    }

    public async Task<IReadOnlySet<Guid>> GetPluginIdsUsedInWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var ids = await _db.PluginToolAudits.AsNoTracking()
            .Where(audit => audit.WorkspaceId == workspaceId && audit.ResultStatus == SuccessStatus)
            .Select(audit => audit.PluginId)
            .Distinct()
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetPluginIdsUsedByUncuratedWorkspaceAsync(
        CancellationToken ct = default)
    {
        var pairs = await _db.PluginToolAudits.AsNoTracking()
            .Where(audit => audit.WorkspaceId != null
                && audit.ResultStatus == SuccessStatus
                && !_db.WorkspacePluginCurations.Any(curation => curation.WorkspaceId == audit.WorkspaceId))
            .Select(audit => new { WorkspaceId = audit.WorkspaceId!.Value, audit.PluginId })
            .Distinct()
            .ToListAsync(ct);

        return pairs
            .GroupBy(pair => pair.WorkspaceId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<Guid>)group.Select(pair => pair.PluginId).ToHashSet());
    }

    public async Task<IReadOnlyDictionary<Guid, PluginUsageByUser>> GetUsageByUserAsync(
        Guid workspaceId,
        Guid pluginId,
        CancellationToken ct = default)
    {
        // Anonymous projection, materialised, then mapped: EF cannot translate a positional record.
        var rows = await _db.PluginToolAudits.AsNoTracking()
            .Where(audit => audit.WorkspaceId == workspaceId
                && audit.PluginId == pluginId
                && audit.ResultStatus == SuccessStatus)
            .GroupBy(audit => audit.UserId)
            .Select(group => new { UserId = group.Key, LastUsedAt = group.Max(audit => audit.CreatedAt), Count = group.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(row => row.UserId, row => new PluginUsageByUser(row.LastUsedAt, row.Count));
    }

    /// <summary>What McpToolOrchestrator writes for a call that worked - "success", never "ok".</summary>
    private const string SuccessStatus = "success";
}
