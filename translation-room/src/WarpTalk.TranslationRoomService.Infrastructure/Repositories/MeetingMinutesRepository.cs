using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class MeetingMinutesRepository : GenericRepository<MeetingMinutes>, IMeetingMinutesRepository
{
    public MeetingMinutesRepository(TranslationRoomDbContext context) : base(context)
    {
    }

    public async Task<MeetingMinutes?> GetCurrentByRoomIdAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _dbSet
            .FirstOrDefaultAsync(m => m.TranslationRoomId == roomId && m.IsCurrent, ct);
    }

    public async Task<List<MeetingMinutes>> GetVersionsByRoomIdAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(m => m.TranslationRoomId == roomId)
            .OrderByDescending(m => m.Version)
            .ToListAsync(ct);
    }

    public async Task<int> CountForWorkspaceYearAsync(Guid workspaceId, int year, CancellationToken ct = default)
    {
        return await _dbSet
            .CountAsync(m => m.WorkspaceId == workspaceId && m.CreatedAt.Year == year, ct);
    }
}
