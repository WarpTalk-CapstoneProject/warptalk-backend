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
}
