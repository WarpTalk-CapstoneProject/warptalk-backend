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
        // Version 1 only: one row per DOCUMENT, not per row in the table.
        //
        // Counting every row made the number skip as soon as anybody revised anything. A workspace
        // holding one document, BB-2026-0007, revised once, counted 2 and handed the next meeting
        // BB-2026-0009 — a gap that reads to anybody looking at the register as a minutes that was
        // written and then destroyed. Worse across a year boundary: revising a 2026 document in
        // January 2027 counted as one 2027 document, so the first real minutes of 2027 came out as
        // BB-2027-0002 and BB-2027-0001 never existed.
        //
        // Version 1 is the row that consumed the number, exactly once per chain, and it stays that
        // row however many revisions follow and whichever one currently holds `is_current`.
        return await _dbSet
            .CountAsync(
                m => m.WorkspaceId == workspaceId && m.Version == 1 && m.CreatedAt.Year == year,
                ct);
    }
}
