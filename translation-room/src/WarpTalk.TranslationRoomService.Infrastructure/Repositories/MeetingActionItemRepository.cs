using WarpTalk.TranslationRoomService.Domain.Constants;
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

public class MeetingActionItemRepository : GenericRepository<MeetingActionItem>, IMeetingActionItemRepository
{
    public MeetingActionItemRepository(TranslationRoomDbContext context) : base(context)
    {
    }

    public async Task<List<MeetingActionItem>> GetByRoomIdAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(item => item.TranslationRoomId == roomId)
            .OrderBy(item => item.AtMs ?? long.MaxValue)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> AnyForMinutesAsync(Guid minutesId, CancellationToken ct = default)
    {
        return await _dbSet.AnyAsync(item => item.SourceMinutesId == minutesId, ct);
    }

    public async Task<List<MeetingActionItem>> GetOpenForSeriesAsync(
        Guid seriesId, Guid excludingRoomId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(item =>
                item.SeriesId == seriesId
                && item.TranslationRoomId != excludingRoomId
                && item.Status == MeetingActionItemConstants.StatusOpen)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<MeetingActionItem>> GetForAssigneeAsync(
        Guid workspaceId, Guid userId, string? status, CancellationToken ct = default)
    {
        var query = _dbSet.Where(item => item.WorkspaceId == workspaceId && item.AssigneeUserId == userId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(item => item.Status == status);
        }

        return await query
            .OrderBy(item => item.Status == MeetingActionItemConstants.StatusOpen ? 0 : 1)
            .ThenByDescending(item => item.CreatedAt)
            .ToListAsync(ct);
    }
}
