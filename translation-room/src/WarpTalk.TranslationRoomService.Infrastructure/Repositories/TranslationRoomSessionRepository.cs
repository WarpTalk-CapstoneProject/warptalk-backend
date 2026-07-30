using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomSessionRepository : GenericRepository<TranslationRoomSession>, ITranslationRoomSessionRepository
{
    public TranslationRoomSessionRepository(TranslationRoomDbContext dbContext) : base(dbContext)
    {
    }

    public async Task<List<TranslationRoomSession>> GetSessionsByRoomIdAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(s => s.TranslationRoomId == roomId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<TranslationRoomSession?> GetActiveSessionByRoomIdAsync(Guid roomId, CancellationToken ct = default)
    {
        var activeStatus = TranslationRoomSessionStatus.ACTIVE.ToString();
        return await _dbSet
            .Where(s => s.TranslationRoomId == roomId && s.Status == activeStatus)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }
}
