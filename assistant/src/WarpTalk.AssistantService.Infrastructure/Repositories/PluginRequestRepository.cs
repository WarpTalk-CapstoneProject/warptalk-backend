using Microsoft.EntityFrameworkCore;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PluginRequestRepository : GenericRepository<PluginRequest>, IPluginRequestRepository
{
    private readonly AssistantDbContext _db;

    public PluginRequestRepository(AssistantDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PluginRequest>> ListForWorkspaceAsync(
        Guid workspaceId,
        string status,
        CancellationToken ct = default)
    {
        return await _db.PluginRequests
            .Where(request => request.WorkspaceId == workspaceId && request.Status == status)
            .OrderBy(request => request.CreatedAt)
            .ThenBy(request => request.Id)
            .ToListAsync(ct);
    }
}
