using Microsoft.EntityFrameworkCore;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PluginConfirmationTokenRepository : GenericRepository<PluginConfirmationToken>, IPluginConfirmationTokenRepository
{
    private readonly AssistantDbContext _db;

    public PluginConfirmationTokenRepository(AssistantDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<bool> TryConsumeAsync(Guid tokenId, DateTime utcNow, CancellationToken ct = default)
    {
        var updated = await _db.PluginConfirmationTokens
            .Where(token => token.Id == tokenId
                && token.ConsumedAt == null
                && token.ExpiresAt > utcNow)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.ConsumedAt, (DateTime?)utcNow), ct);

        return updated == 1;
    }
}
