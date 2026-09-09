using Microsoft.EntityFrameworkCore;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PluginConnectionRepository : GenericRepository<PluginConnection>, IPluginConnectionRepository
{
    private readonly AssistantDbContext _db;

    public PluginConnectionRepository(AssistantDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<int> CountForPluginAsync(Guid pluginId, CancellationToken ct = default)
        => await _db.PluginConnections.CountAsync(connection => connection.PluginId == pluginId, ct);
}
