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
        // Only rows whose workspace has a curation record. The marketplace writes the two together,
        // so today this excludes nothing - but a list row without a curation record is one
        // WorkspacePluginAvailability never reads (an uncurated workspace is judged by the legacy
        // switch alone), and counting it would report a workspace as having a plugin it cannot use.
        //
        // Projected into an anonymous type and materialised before anything else touches it: EF
        // cannot translate an ordering over a positional-record projection.
        // Only rows whose workspace has a curation record: an uncurated workspace is judged by its
        // carried-over usage, never by list rows (WorkspacePluginAvailability), so a stray row there
        // would count a workspace as having a plugin it cannot use.
        var counts = await _db.WorkspacePlugins
            .Where(row => _db.WorkspacePluginCurations.Any(curation => curation.WorkspaceId == row.WorkspaceId))
            .GroupBy(row => row.PluginId)
            .Select(group => new { PluginId = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(entry => entry.PluginId, entry => entry.Count);
    }
}
