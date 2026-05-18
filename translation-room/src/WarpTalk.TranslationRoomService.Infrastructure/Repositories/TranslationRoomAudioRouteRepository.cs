using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomAudioRouteRepository : GenericRepository<TranslationRoomAudioRoute>, ITranslationRoomAudioRouteRepository
{
    public TranslationRoomAudioRouteRepository(TranslationRoomDbContext dbContext) : base(dbContext)
    {
    }

    public async Task<List<TranslationRoomAudioRoute>> GetRoutesByRoomIdAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(r => r.TranslationRoomId == roomId)
            .ToListAsync(ct);
    }

    public async Task<List<TranslationRoomAudioRoute>> GetRoutesByStatusAsync(AudioRouteStatus status, CancellationToken ct = default)
    {
        var statusStr = status.ToString();
        return await _dbSet
            .Where(r => r.Status == statusStr)
            .ToListAsync(ct);
    }

    public Task UpdateRoutesAsync(IEnumerable<TranslationRoomAudioRoute> routes, CancellationToken ct = default)
    {
        _dbSet.UpdateRange(routes);
        return Task.CompletedTask;
    }

    public async Task AddRoutesAsync(IEnumerable<TranslationRoomAudioRoute> routes, CancellationToken ct = default)
    {
        await _dbSet.AddRangeAsync(routes, ct);
    }
}
