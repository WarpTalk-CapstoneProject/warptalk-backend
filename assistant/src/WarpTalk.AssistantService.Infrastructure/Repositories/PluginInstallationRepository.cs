using Microsoft.EntityFrameworkCore;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PluginInstallationRepository : GenericRepository<PluginInstallation>, IPluginInstallationRepository
{
    private readonly AssistantDbContext _db;

    public PluginInstallationRepository(AssistantDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountByPluginAsync(CancellationToken ct = default)
    {
        // Installed only. Removing a plugin keeps its row as 'disabled' (so its tool policy survives a
        // reinstall), and counting those reported every user who ever tried a plugin as a live install.
        var counts = await _db.PluginInstallations
            .Where(installation => installation.Status == PluginConstants.InstallationStatus.Installed)
            .GroupBy(installation => installation.PluginId)
            .Select(group => new { PluginId = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(entry => entry.PluginId, entry => entry.Count);
    }

    public async Task<int> CountForPluginAsync(Guid pluginId, CancellationToken ct = default)
        => await _db.PluginInstallations.CountAsync(installation => installation.PluginId == pluginId, ct);
}
