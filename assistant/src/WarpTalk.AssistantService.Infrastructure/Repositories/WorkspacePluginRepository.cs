using Microsoft.EntityFrameworkCore;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class WorkspacePluginRepository : GenericRepository<WorkspacePlugin>, IWorkspacePluginRepository
{
    private readonly AssistantDbContext _db;

    public WorkspacePluginRepository(AssistantDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountWorkspacesByPluginAsync(CancellationToken ct = default)
    {
        // Projected into an anonymous type and materialised before anything else touches it: EF
        // cannot translate an ordering over a positional-record projection.
        var counts = await _db.WorkspacePlugins
            .GroupBy(row => row.PluginId)
            .Select(group => new { PluginId = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(entry => entry.PluginId, entry => entry.Count);
    }
}
